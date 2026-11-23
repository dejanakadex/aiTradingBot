using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

/// <summary>Restartable projection of durable fills, never a broker callback or order sender.</summary>
public sealed class CompletedTradeAnalysisService(IDbContextFactory<TradingBotDbContext> factory, IClock clock,
    ILogger<CompletedTradeAnalysisService> logger, IOptions<TradingSettings> settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ProjectionVersion = "post-trade-executions-v1";
    public async Task<int> SynchronizeBatchAsync(int afterId = 0, int batchSize = 100, CancellationToken ct = default)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var scan = await factory.CreateDbContextAsync(ct);
        var ids = await scan.ExitManagementRecords.AsNoTracking().Where(x => x.Id > afterId && x.State == ExitManagementState.Closed && x.TradeId != null)
            .OrderBy(x => x.Id).Select(x => x.Id).Take(batchSize).ToArrayAsync(ct);
        foreach (var id in ids)
        {
            try { await SynchronizeAsync(id, ct); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // A malformed historical record must not starve other closed trades; retry it on the next scan.
                logger.LogError("Post-trade lifecycle {LifecycleId} has invalid reconstruction input ({ErrorType}); deferred for repair.", id, ex.GetType().Name);
            }
        }
        return ids.LastOrDefault();
    }

    public async Task<bool> SynchronizeAsync(int exitId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // SQLite serializable transaction keeps the source snapshot and projection update atomic across concurrent workers/fee callbacks.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var exit = await db.ExitManagementRecords.SingleOrDefaultAsync(x => x.Id == exitId, ct);
        if (exit?.State != ExitManagementState.Closed || exit.TradeId == null || exit.ClosedUtc == null
            || exit.FilledQuantity <= 0 || exit.ExitedQuantity != exit.FilledQuantity || exit.EntryRemainingQuantity != 0) return false;
        if (settings.Value.GetConfiguredInstruments().SingleOrDefault(x => x.InstrumentId == exit.InstrumentId)?.Currency != "USD")
        {
            logger.LogWarning("Post-trade projection deferred for lifecycle {LifecycleId}: supported USD quote currency is required.", exitId);
            return false;
        }
        var trade = await db.Trades.SingleOrDefaultAsync(x => x.Id == exit.TradeId, ct);
        if (trade?.ClosedUtc == null) return false;
        var arbitrationIds = await db.SignalArbitrationRecords.Where(x => x.SignalId == exit.SignalId && x.InstrumentId == exit.InstrumentId && x.StrategyId == exit.StrategyId).Select(x => x.Id).ToArrayAsync(ct);
        var linked = await db.VirtualAllocationOrderRecords.Where(x => arbitrationIds.Contains(x.ArbitrationId)).Select(x => x.BrokerOrderId).ToArrayAsync(ct);
        var brokerIds = linked.Concat(new[] { exit.EntryBrokerOrderId, exit.ProtectiveStopBrokerOrderId, exit.TakeProfitBrokerOrderId, exit.ExitBrokerOrderId }).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        var orders = await db.OrderRecords.AsNoTracking().Where(x => brokerIds.Contains(x.BrokerOrderId)).OrderBy(x => x.Id).ToArrayAsync(ct);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var fills = await db.ExecutionRecords.AsNoTracking().Where(x => orderIds.Contains(x.OrderRecordId)).OrderBy(x => x.BrokerExecutionId).ToArrayAsync(ct);
        var entries = orders.Where(x => x.BrokerOrderId == exit.EntryBrokerOrderId && x.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
        var exits = orders.Where(x => x.BrokerOrderId != exit.EntryBrokerOrderId && x.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase)).Select(x => x.Id).ToHashSet();
        var buys = fills.Where(x => entries.Contains(x.OrderRecordId)).ToArray(); var sells = fills.Where(x => exits.Contains(x.OrderRecordId)).ToArray();
        if (fills.Length == 0 || fills.Any(x => x.Quantity <= 0 || x.Price <= 0) || buys.Length + sells.Length != fills.Length
            || buys.Sum(x => x.Quantity) != exit.FilledQuantity || sells.Sum(x => x.Quantity) != exit.FilledQuantity)
        {
            logger.LogWarning("Post-trade projection deferred for lifecycle {LifecycleId}: complete reconciled BUY/SELL executions are required.", exitId);
            return false;
        }
        var proofs = await (from r in db.PortfolioRiskReservationRecords join p in db.ForexExecutionProofRecords on r.Id equals p.ReservationId
            where r.SignalId == exit.SignalId && r.InstrumentId == exit.InstrumentId
            orderby p.ReservationId select new { p.ReservationId, p.RequestJson, p.AssessmentJson }).ToArrayAsync(ct);
        var decisions = await db.ResearchCandidateRecords.AsNoTracking().Where(x => exit.SignalId != null && x.SignalId == exit.SignalId && x.InstrumentId == exit.InstrumentId)
            .OrderBy(x => x.Id).Select(x => new { x.CandidateKey, x.FeatureVersion, x.PatternVersion, x.DecisionStage, x.MetadataJson, x.ReasonsJson }).ToArrayAsync(ct);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = ProjectionVersion, exit.Id, exit.TradeId, exit.SignalId, exit.InstrumentId, exit.StrategyId, exit.RawJson, exit.ExitReason,
            exit.FilledQuantity, exit.ClosedUtc, orders, fills, proofs, decisions
        }, Json))));
        var record = await db.PostTradeAnalysisRecords.SingleOrDefaultAsync(x => x.ExitManagementId == exit.Id, ct);
        if (record?.InputHash == fingerprint) return false;
        using var context = JsonDocument.Parse(string.IsNullOrWhiteSpace(exit.RawJson) ? "{}" : exit.RawJson);
        string Part(string name) => context.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : "null";
        string Text(string name, string property) => context.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var field) ? field.ToString() : "Unknown";
        bool UsdFee(ExecutionRecord fill)
        {
            if (!fill.Commission.HasValue || string.IsNullOrWhiteSpace(fill.RawJson)) return false;
            using var raw = JsonDocument.Parse(fill.RawJson);
            return raw.RootElement.TryGetProperty("commissionCurrency", out var currency) && currency.GetString() == "USD";
        }
        var knownFees = fills.Where(UsdFee).ToArray(); var feesComplete = knownFees.Length == fills.Length;
        var quantity = exit.FilledQuantity; var buyValue = buys.Sum(x => x.Price * x.Quantity); var sellValue = sells.Sum(x => x.Price * x.Quantity);
        var fees = knownFees.Sum(x => x.Commission!.Value); var gross = sellValue - buyValue; var net = gross - fees;
        var filledExitOrders = orders.Where(x => sells.Any(fill => fill.OrderRecordId == x.Id)).ToArray();
        var exitReason = !string.IsNullOrWhiteSpace(exit.ExitReason) ? exit.ExitReason
            : filledExitOrders.All(x => x.BrokerOrderId == exit.ProtectiveStopBrokerOrderId) ? "ProtectiveStop"
            : filledExitOrders.All(x => x.BrokerOrderId == exit.TakeProfitBrokerOrderId) ? "TakeProfit" : "MixedExitExecutions";
        record ??= new PostTradeAnalysisRecord { ExitManagementId = exit.Id, CreatedUtc = clock.UtcNow };
        if (record.Id == 0) db.PostTradeAnalysisRecords.Add(record);
        record.SourceTradeId = trade.Id; record.InstrumentId = exit.InstrumentId; record.StrategyId = exit.StrategyId; record.SignalId = exit.SignalId;
        record.Symbol = exit.Symbol; record.Pattern = Text("pattern", "patternType");
        if (int.TryParse(record.Pattern, out var pattern) && Enum.IsDefined(typeof(PatternType), pattern)) record.Pattern = ((PatternType)pattern).ToString();
        record.Quantity = quantity; record.EntryPrice = buyValue / quantity; record.ExitPrice = sellValue / quantity;
        record.OpenedUtc = buys.Min(x => x.TimestampUtc); record.ClosedUtc = sells.Max(x => x.TimestampUtc);
        if (record.ClosedUtc < record.OpenedUtc) throw new InvalidDataException("Exit execution predates entry execution.");
        record.GrossProfitLoss = gross; record.CommissionsFees = fees; record.NetProfitLoss = net; record.FeesComplete = feesComplete;
        record.HoldingDurationMinutes = (decimal)(record.ClosedUtc - record.OpenedUtc).TotalMinutes;
        record.TradeResult = !feesComplete ? "ProvisionalFees" : net > 0 ? "Win" : net < 0 ? "Loss" : "BreakEven";
        record.MarketSnapshotJson = Part("snapshot"); record.PatternCandidateJson = Part("pattern");
        record.AiMarketAnalysisJson = Part("aiAnalysis"); record.AiCriticAnalysisJson = Part("aiCriticAnalysis");
        record.StrategyDecisionJson = Part("strategyDecision"); record.RiskDecisionJson = Part("riskDecision");
        record.MarketRegime = Text("aiAnalysis", "marketRegime"); record.AiDecision = Text("aiAnalysis", "action");
        record.CriticDecision = Text("aiCriticAnalysis", "approved"); record.StrategyDecision = Text("strategyDecision", "approved");
        record.RiskDecision = Text("riskDecision", "decision"); record.FifteenMinuteTrend = "Unknown";
        record.OrderDataJson = JsonSerializer.Serialize(orders, Json); record.ExecutionDataJson = JsonSerializer.Serialize(fills, Json);
        record.EntryContextJson = JsonSerializer.Serialize(new { lifecycleContext = context.RootElement, proofs, decisions }, Json);
        record.TradeResultJson = JsonSerializer.Serialize(new { version = ProjectionVersion, quantity, entryPrice = record.EntryPrice, exitPrice = record.ExitPrice,
            grossProfitLoss = gross, commissionsFees = fees, netProfitLoss = net, feesComplete, currency = "USD",
            exitReason, lifecycleClosedUtc = exit.ClosedUtc, result = record.TradeResult,
            limitation = "Execution-weighted long result; no inferred excursions. Incomplete/non-USD fees remain provisional." }, Json);
        record.InputHash = fingerprint; record.UpdatedUtc = clock.UtcNow;
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        logger.LogInformation("Post-trade lifecycle {LifecycleId} projected to record {AnalysisId}; feesComplete={FeesComplete}.", exit.Id, record.Id, feesComplete);
        return true;
    }
}
