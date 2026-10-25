using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

/// <summary>Explicit FX paper preparation, used after strategy/model approval. Does not manufacture a model
/// forecast or promote FX shadow candidates. Uses the same reservation store, arbitration and order lifecycle.</summary>
public sealed class ForexPaperPreparationService(
    IOptions<TradingSettings> trading, IOptions<IbkrSettings> ibkr, IOptions<RiskSettings> limits,
    IOptions<TradingBot.Infrastructure.Options.ForexExecutionSettings> execution,
    IOperatingModeService mode, ITradingEngineStatusService status, IInstrumentRegistryService registry,
    IAccountService accounts, IPositionService positions, IOrderExecutionService orders, IForexBrokerRules brokerRules,
    ILatestMarketDataService latest, ForexRiskService calculator, PortfolioRiskService portfolio,
    ISignalArbitrationService arbitration, IDbContextFactory<TradingBotDbContext> factory, IClock clock)
{
    public async Task<ApprovedTradePlan> PrepareAsync(ApprovedTradePlan proposal, CancellationToken cancellationToken = default)
    {
        var instrument = await ValidateAsync(proposal, cancellationToken);
        var accountId = ibkr.Value.PaperAccountId!;
        var brokerPositions = (await positions.GetPositionsAsync(accountId, cancellationToken)).ToArray();
        var intent = await arbitration.ArbitrateAsync(proposal.Pattern, accountId, brokerPositions, cancellationToken);
        if (!intent.Approved || !intent.ArbitrationId.HasValue) throw new InvalidOperationException(intent.Reason);
        Guid? reserved = null;
        try
        {
            var prepared = await portfolio.ReserveForexAsync(proposal.StrategyDecision, accountId, calculator,
                (active, ct) => RefreshAsync(proposal, instrument, active, proposal.StrategyDecision.TradeSignal!.SuggestedQuantity, ct), cancellationToken);
            reserved = prepared.Risk.ReservationId;
            await arbitration.BindReservationAsync(intent.ArbitrationId.Value, reserved!.Value, prepared.Risk.ApprovedQuantity,
                prepared.Risk.PositionValue, cancellationToken);
            var a = prepared.Assessment;
            var original = proposal.StrategyDecision;
            return new ApprovedTradePlan
            {
                Pattern = proposal.Pattern, Snapshot = proposal.Snapshot, AiAnalysis = proposal.AiAnalysis, AiCriticAnalysis = proposal.AiCriticAnalysis,
                StrategyDecision = new() { Symbol = original.Symbol, Approved = true, Context = original.Context, TradeSignal = original.TradeSignal,
                    EvaluatedAtUtc = original.EvaluatedAtUtc, EntryMin = a.EntryPrice, EntryMax = a.EntryPrice,
                    StopPrice = a.StopPrice, TakeProfitPrice = a.TargetPrice, ExpectedRewardRiskRatio = original.ExpectedRewardRiskRatio },
                RiskDecision = prepared.Risk, ArbitrationId = intent.ArbitrationId, ApprovedAtUtc = clock.UtcNow
            };
        }
        catch
        {
            if (reserved.HasValue) await portfolio.ReleaseAsync(reserved.Value, "FX preparation failed before submission.", CancellationToken.None);
            await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
            // A concurrent preparation may own this same signal. Never reject its winning reservation.
            if (reserved.HasValue || !await db.PortfolioRiskReservationRecords.AnyAsync(x => x.SignalId == proposal.Context.SignalId))
                await arbitration.RejectAsync(intent.ArbitrationId.Value, "FX preparation failed before submission.", CancellationToken.None);
            throw;
        }
    }

    public async Task<ScalpingExecutionDecision> RevalidateAsync(ApprovedTradePlan plan, CancellationToken cancellationToken)
    {
        try
        {
            var instrument = await ValidateAsync(plan, cancellationToken);
            if (plan.RiskDecision?.ReservationId is not Guid id) throw new InvalidOperationException("FX reservation is missing.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var reservation = await db.PortfolioRiskReservationRecords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            var proof = await db.ForexExecutionProofRecords.SingleOrDefaultAsync(x => x.ReservationId == id, cancellationToken);
            if (reservation == null || proof == null || reservation.Status != PortfolioReservationStatus.Pending || reservation.ExpiresAtUtc <= clock.UtcNow
                || reservation.SignalId != plan.Context.SignalId || reservation.AccountId != ibkr.Value.PaperAccountId
                || reservation.Quantity != plan.RiskDecision.ApprovedQuantity)
                throw new InvalidOperationException("FX reservation/proof is expired, used or mismatched.");
            var active = await db.PortfolioRiskReservationRecords.Where(x => x.Id != id && x.AccountId == reservation.AccountId
                && (x.Status == PortfolioReservationStatus.Pending || x.Status == PortfolioReservationStatus.Committed)).ToArrayAsync(cancellationToken);
            var request = await RefreshAsync(plan, instrument, active, reservation.Quantity, cancellationToken);
            var original = JsonSerializer.Deserialize<ForexRiskRequest>(proof.RequestJson)
                ?? throw new InvalidOperationException("FX reservation proof is unreadable.");
            if (original.InstrumentId != instrument.InstrumentId || original.StrategyId != plan.Context.StrategyId
                || (original.Bid + original.Ask) / 2m * plan.AiAnalysis.ExpectedMovePercent / 100m != original.ExpectedMidpointMove
                || JsonSerializer.Serialize(original.Rules) != JsonSerializer.Serialize(request.Rules))
                throw new InvalidOperationException("FX forecast or reviewed broker rules changed after reservation.");
            var a = calculator.Evaluate(request);
            if (!a.Passed || a.BaseUnits != reservation.Quantity || a.StopRiskUsd > reservation.RiskAmount
                || a.NotionalUsd > reservation.PositionValue || a.EntryPrice != plan.StrategyDecision.EntryMin
                || a.StopPrice != plan.StrategyDecision.StopPrice || a.TargetPrice != plan.StrategyDecision.TakeProfitPrice)
                throw new InvalidOperationException("FX price/risk/capacity changed after reservation; a new signal is required. " + string.Join(" ", a.Reasons));
            var seconds = instrument.MaximumHoldingSeconds ?? 0;
            if (seconds <= 0) throw new InvalidOperationException("FX entry requires an explicit maximum holding time in seconds.");
            var cost = new ExecutionCostEstimate(ScalpingOrderPolicy.MarketableLimit, a.EntryPrice,
                a.SpreadUsd / a.NotionalUsd * 10000m, a.CommissionUsd / a.NotionalUsd * 10000m,
                a.SlippageUsd / a.NotionalUsd * 10000m, 0m, a.TotalCostUsd / a.NotionalUsd * 10000m,
                a.ExpectedGrossProfitUsd / a.NotionalUsd * 10000m, a.ExpectedNetEdgeBps);
            return new(true, "Fresh FX risk and durable reservation verified.", clock.UtcNow, request.QuoteEventUtc,
                request.Bid, request.Ask, a.BaseUnits, seconds, cost.Policy, OrderType.Limit, a.EntryPrice, cost, [cost], []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
        { return ScalpingExecutionDecision.Reject(clock.UtcNow, ex.Message, [ex.Message]); }
    }

    private static void ValidateCommission(ExecutionRecord fee)
    {
        if (fee.Commission == null || string.IsNullOrWhiteSpace(fee.RawJson))
            throw new InvalidOperationException("Execution commission or currency is unknown.");
        using var payload = JsonDocument.Parse(fee.RawJson);
        if (!payload.RootElement.TryGetProperty("commissionCurrency", out var currency)
            || currency.ValueKind != JsonValueKind.String || currency.GetString() != "USD")
            throw new InvalidOperationException("Unknown or non-USD commission currency requires reconciliation before FX entry.");
    }

    private async Task<ConfiguredInstrument> ValidateAsync(ApprovedTradePlan plan, CancellationToken ct)
    {
        var instrument = trading.Value.GetConfiguredInstruments().Single(x => x.InstrumentId == plan.Context.InstrumentId);
        var state = status.Current;
        if (!MarketDataSession.IsForexOpen(clock.UtcNow)) throw new InvalidOperationException("FX session is closed.");
        var registered = await registry.GetAsync(instrument.InstrumentId, ct);
        if (!trading.Value.Enabled || !trading.Value.ForexPaperExecutionEnabled || !instrument.TradingEnabled
            || instrument.InstrumentType != InstrumentType.Forex || instrument.Currency != "USD"
            || mode.CurrentMode != TradingOperatingMode.PaperTrading || state.BrokerEnvironmentVerification != BrokerEnvironmentVerificationStatus.VerifiedPaper
            || !state.ReconciliationCompleted || state.State != TradingEngineState.Ready || !state.TradingEnabled || registered?.CanSubmitPaperOrders != true)
            throw new InvalidOperationException("FX paper entry requires enabled USD-quoted instrument, PaperReady registry and reconciled verified-paper state.");
        if (string.IsNullOrWhiteSpace(ibkr.Value.PaperAccountId) || plan.Pattern.Direction != TradeDirection.Long
            || plan.StrategyDecision.Context != plan.Context || plan.StrategyDecision.Symbol != instrument.Symbol
            || !plan.StrategyDecision.Approved || plan.StrategyDecision.TradeSignal?.Action != TradeAction.Enter
            || plan.AiAnalysis.Action != AiMarketActions.Buy || plan.AiAnalysis.IsSafeFallback || plan.AiAnalysis.ExpectedMovePercent <= 0
            || !plan.Context.PatternVersion.StartsWith(PipelineContractVersions.ForexPatterns, StringComparison.Ordinal))
            throw new InvalidOperationException("FX requires a matching approved long strategy and explicit forecast; shadow confidence is not a forecast.");
        if (clock.UtcNow - plan.StrategyDecision.EvaluatedAtUtc > TimeSpan.FromSeconds(5) || plan.StrategyDecision.EvaluatedAtUtc > clock.UtcNow)
            throw new InvalidOperationException("FX strategy decision is stale or future.");
        return instrument;
    }

    private async Task<ForexRiskRequest> RefreshAsync(ApprovedTradePlan proposal, ConfiguredInstrument instrument,
        IReadOnlyList<PortfolioRiskReservationRecord> pending, decimal maximumUnits, CancellationToken ct)
    {
        var accountId = ibkr.Value.PaperAccountId!;
        var broker = await brokerRules.GetForexOrderRulesAsync(instrument, ct);
        var observed = clock.UtcNow;
        var account = await accounts.GetAccountInfoAsync(accountId, ct);
        if (account.AccountId != accountId || account.Currency != "USD")
            throw new InvalidOperationException("FX-9 execution requires a verified USD account summary; other account currencies remain research-only.");
        var held = (await positions.GetPositionsAsync(accountId, ct)).Where(x => x.Quantity != 0).ToArray();
        var open = (await orders.GetOpenOrdersAsync(ct)).ToArray();
        await using var db = await factory.CreateDbContextAsync(ct);
        var knownOrders = await db.OrderRecords.AsNoTracking().Select(x => x.BrokerOrderId).ToArrayAsync(ct);
        if (open.Any(x => !knownOrders.Contains(x.BrokerOrderId ?? x.OrderId) || x.AccountId != null && x.AccountId != accountId))
            throw new InvalidOperationException("Unknown or foreign broker orders require reconciliation before FX entry.");
        var snapshot = new List<ForexExposure>();
        foreach (var position in held)
        {
            var configured = trading.Value.GetConfiguredInstruments().SingleOrDefault(x => x.Symbol == position.Symbol);
            if (configured == null || configured.Currency != "USD" || position.AccountId != accountId)
                throw new InvalidOperationException("Unvalued broker position blocks FX entry.");
            var quote = latest.Get(configured.InstrumentId);
            if (quote?.Ask is not > 0 || quote.AskTimeUtc == null || clock.UtcNow - quote.AskTimeUtc > TimeSpan.FromSeconds(5) || quote.AskTimeUtc > clock.UtcNow)
                throw new InvalidOperationException("A fresh valuation quote is required for every open position.");
            var allocations = await db.SignalArbitrationRecords.AsNoTracking().Where(x => x.AccountId == accountId
                && x.InstrumentId == configured.InstrumentId && x.FilledQuantity > x.ExitedQuantity).ToArrayAsync(ct);
            if (allocations.Sum(x => x.FilledQuantity - x.ExitedQuantity) != position.Quantity)
                throw new InvalidOperationException("Position has no complete strategy allocation; reconcile before FX entry.");
            foreach (var allocation in allocations)
            {
                var allocationValue = (allocation.FilledQuantity - allocation.ExitedQuantity) * quote.Ask.Value;
                snapshot.Add(new(configured.InstrumentId, allocation.StrategyId, new(allocationValue, "USD"),
                    // USD equivalents for the base leg are converted using this same marked pair quote below.
                    configured.BaseCurrency == null ? [new(allocationValue, "USD")]
                    : [new(allocation.FilledQuantity - allocation.ExitedQuantity, configured.BaseCurrency), new(-allocationValue, "USD")]));
            }
        }
        // Conservative overlap: committed notional remains reserved until lifecycle release, even while filled.
        // It is never silently freed by a timeout or by a disconnected snapshot.
        foreach (var item in pending)
        {
            var configured = trading.Value.GetConfiguredInstruments().SingleOrDefault(x => x.InstrumentId == item.InstrumentId)
                ?? throw new InvalidOperationException("Unvalued pending reservation.");
            snapshot.Add(new(item.InstrumentId, item.StrategyId, new(item.SignedExposure, "USD"), configured.BaseCurrency == null
                ? [new(item.SignedExposure, "USD")] : [new(item.Quantity, configured.BaseCurrency), new(-item.PositionValue, "USD")]));
        }
        var now = clock.UtcNow;
        var profile = execution.Value.Profiles.Single(x => x.InstrumentId == instrument.InstrumentId);
        var quoteBar = latest.GetCompletedForexAggregates(instrument.InstrumentId, profile.TrailingIntervalSeconds, now, 1).LastOrDefault()
            ?? throw new InvalidOperationException("A completed paired FX quote is required.");
        var conversions = new List<UsdConversionQuote>();
        foreach (var currency in snapshot.SelectMany(x => x.CurrencyLegs).Select(x => x.Currency).Where(x => x != "USD").Distinct())
        {
            var pair = trading.Value.GetConfiguredInstruments().SingleOrDefault(x => x.BaseCurrency == currency && x.Currency == "USD")
                ?? throw new InvalidOperationException("Missing USD conversion instrument.");
            var q = latest.GetCompletedForexAggregates(pair.InstrumentId, profile.TrailingIntervalSeconds, now, 1).LastOrDefault()
                ?? throw new InvalidOperationException("Missing paired USD conversion quote.");
            conversions.Add(new(currency, q.LastBid, q.LastAsk, q.LastQuoteTimeUtc, q.AvailableAtUtc));
        }
        var today = now.Date;
        var closed = await db.ExitManagementRecords.AsNoTracking().Where(x => x.ClosedUtc >= today && x.State == ExitManagementState.Closed)
            .OrderByDescending(x => x.ClosedUtc).ToArrayAsync(ct);
        var grossPnl = new List<decimal>();
        var netPnl = new List<decimal>();
        var olderClosedFees = new Dictionary<int, decimal>();
        foreach (var trade in closed)
        {
            if (trade.SignalId == null) throw new InvalidOperationException("Closed trade allocation is incomplete.");
            var fills = await (from allocation in db.SignalArbitrationRecords.AsNoTracking()
                join link in db.VirtualAllocationOrderRecords on allocation.Id equals link.ArbitrationId
                join order in db.OrderRecords on link.BrokerOrderId equals order.BrokerOrderId
                join fill in db.ExecutionRecords on order.Id equals fill.OrderRecordId
                where allocation.AccountId == accountId && allocation.SignalId == trade.SignalId
                select new { link.IsEntry, Fill = fill }).ToArrayAsync(ct);
            if (fills.Sum(x => x.IsEntry ? x.Fill.Quantity : 0m) != trade.FilledQuantity
                || fills.Sum(x => !x.IsEntry ? x.Fill.Quantity : 0m) != trade.FilledQuantity
                || fills.Any(x => x.Fill.Quantity <= 0 || x.Fill.Price <= 0))
                throw new InvalidOperationException("Closed trade individual fills are incomplete; reconcile before FX entry.");
            foreach (var fill in fills)
            {
                ValidateCommission(fill.Fill);
                if (fill.Fill.TimestampUtc < today) olderClosedFees[fill.Fill.Id] = fill.Fill.Commission!.Value;
            }
            var gross = fills.Sum(x => (x.IsEntry ? -1m : 1m) * x.Fill.Quantity * x.Fill.Price);
            grossPnl.Add(gross);
            netPnl.Add(gross - fills.Sum(x => x.Fill.Commission!.Value));
        }
        var fees = await db.ExecutionRecords.AsNoTracking().Where(x => x.TimestampUtc >= today).ToArrayAsync(ct);
        foreach (var fee in fees) ValidateCommission(fee);
        // Conservative daily loss budget: no offset by profitable trades, includes today's open-position fees.
        var losses = grossPnl.Where(x => x < 0).Sum() - fees.Sum(x => x.Commission!.Value) - olderClosedFees.Values.Sum();
        var consecutive = netPnl.TakeWhile(x => x <= 0).Count();
        if (closed.Length >= limits.Value.MaximumTradesPerDay || consecutive >= limits.Value.MaximumConsecutiveLosses
            || (consecutive >= limits.Value.LossCooldownAfterConsecutiveLosses && consecutive > 0
                && closed[0].ClosedUtc > now.AddMinutes(-limits.Value.LossCooldownDurationMinutes)))
            throw new InvalidOperationException("FX daily trade/loss cooldown limits are active.");
        var reservedFees = 0m;
        foreach (var item in pending)
        {
            var proof = await db.ForexExecutionProofRecords.FindAsync([item.Id], ct);
            if (proof != null) reservedFees += JsonSerializer.Deserialize<ForexRiskAssessment>(proof.AssessmentJson)!.CommissionUsd;
        }
        var request = new ForexRiskRequest(instrument.InstrumentId, proposal.Context.StrategyId, TradeDirection.Long, now,
            quoteBar.LastBid, quoteBar.LastAsk, quoteBar.LastQuoteTimeUtc, quoteBar.AvailableAtUtc,
            proposal.StrategyDecision.StopPrice ?? 0m, proposal.StrategyDecision.TakeProfitPrice ?? 0m,
            (quoteBar.LastBid + quoteBar.LastAsk) / 2m * proposal.AiAnalysis.ExpectedMovePercent / 100m, maximumUnits,
            broker.Contract, broker.ObservedUtc, broker.Rules,
            new(observed, true, new(account.NetLiquidation, "USD"),
                new(Math.Min(account.AvailableFunds, account.BuyingPower) - pending.Sum(x => x.PositionValue) - reservedFees, "USD"),
                new(losses, "USD"), snapshot, snapshot.Count), conversions);
        var assessedEntry = ForexRiskService.RoundPrice(request.Ask, true, broker.Rules.PriceBands);
        if (assessedEntry < proposal.StrategyDecision.EntryMin || assessedEntry > proposal.StrategyDecision.EntryMax)
            throw new InvalidOperationException("FX executable entry is outside the approved strategy range.");
        return request;
    }
}
