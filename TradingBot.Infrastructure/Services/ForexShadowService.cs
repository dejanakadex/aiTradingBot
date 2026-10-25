using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

/// <summary>Measures model proposals only. No risk reservation, order channel or broker dependency.</summary>
public sealed class ForexShadowService(ForexModelDecisionService decisions, ForexResearchContract contracts,
    IDbContextFactory<TradingBotDbContext> factory, ILatestMarketDataService latest,
    IOptions<TradingSettings> trading, IClock clock)
{
    public const string Policy = "FX.ModelShadow.v1";
    public sealed record Proof(string Feature, string Pattern, string Label, string ModelIdentity,
        string Mode, double ElapsedMilliseconds, double? SignalAgeMilliseconds = null, double? QuoteAgeMilliseconds = null);

    public async Task ObserveAsync(PatternCandidate candidate, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.ScalpingExecutionDecisionRecords.AnyAsync(x => x.SignalId == candidate.Context.SignalId && x.SelectedPolicy == Policy, ct)) return;
        var now = clock.UtcNow;
        var quote = latest.Get(candidate.InstrumentId);
        var watch = Stopwatch.StartNew();
        ApprovedTradePlan? proposal = null;
        var label = "unavailable";
        var reason = "Model proposal passed; risk, account, broker rules and execution permissions are separate gates.";
        try
        {
            label = contracts.Get(candidate.InstrumentId, candidate.StrategyId).Label;
            proposal = await decisions.CreateProposalAsync(candidate, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Gate messages contain configuration diagnostics only; never persist broker/account payloads.
            reason = "Model proposal blocked: " + ex.Message;
        }
        watch.Stop();
        var costs = proposal?.Pattern.Metadata.TryGetValue("modelResidualCostsBps", out var value) == true
            && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var gross = (proposal?.AiAnalysis.ExpectedMovePercent ?? 0) * 100;
        var signalAge = candidate.Metadata.TryGetValue("decisionAtUtc", out var time)
            && DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var decisionAt) && decisionAt.Kind == DateTimeKind.Utc
            ? (now - decisionAt).TotalMilliseconds : (double?)null;
        db.ScalpingExecutionDecisionRecords.Add(new()
        {
            SignalId = candidate.Context.SignalId, InstrumentId = candidate.InstrumentId, StrategyId = candidate.StrategyId,
            Symbol = candidate.Symbol, EvaluatedAtUtc = now, QuoteAsOfUtc = quote?.BidTimeUtc,
            Approved = proposal != null, SelectedPolicy = Policy, Bid = quote?.Bid ?? 0, Ask = quote?.Ask ?? 0,
            Quantity = proposal?.StrategyDecision.TradeSignal?.SuggestedQuantity ?? 0,
            ExpectedGrossEdgeBps = gross, EstimatedCostBps = costs, ExpectedNetEdgeBps = gross - costs,
            Reason = reason,
            DecisionJson = JsonSerializer.Serialize(new Proof(candidate.Context.FeatureVersion, candidate.PatternVersion, label,
                proposal?.AiAnalysis.Model ?? "", trading.Value.OperatingMode.ToString(), watch.Elapsed.TotalMilliseconds,
                signalAge, quote?.BidTimeUtc is { } quoteTime ? (now - quoteTime).TotalMilliseconds : null))
        });
        await db.SaveChangesAsync(ct);
    }

    public static Proof? ReadProof(ScalpingExecutionDecisionRecord record)
    {
        if (record.SelectedPolicy != Policy) return null;
        try { return JsonSerializer.Deserialize<Proof>(record.DecisionJson); }
        catch (JsonException) { return null; }
    }
}
