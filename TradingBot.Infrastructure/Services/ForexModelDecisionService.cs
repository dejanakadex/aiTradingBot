using System.Globalization;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

/// <summary>Converts an explicitly approved FX model into a proposal. FX-9 still owns all execution gates.</summary>
public sealed class ForexModelDecisionService(ForexResearchContract contracts, INumericalModelService models,
    ILatestMarketDataService latest, IOptions<TradingSettings> trading, IOptions<ExitStrategySettings> exits,
    IOptions<ForexExecutionSettings> execution, IClock clock)
{
    public async Task<ApprovedTradePlan> CreateProposalAsync(PatternCandidate candidate, CancellationToken ct = default)
    {
        var c = contracts.Get(candidate.InstrumentId, candidate.StrategyId);
        var instrument = trading.Value.GetConfiguredInstruments().Single(x => x.InstrumentId == candidate.InstrumentId);
        var now = clock.UtcNow;
        if (candidate.Direction != TradeDirection.Long || candidate.Context.FeatureVersion != c.Feature || candidate.PatternVersion != c.Pattern
            || !candidate.Metadata.TryGetValue("decisionAtUtc", out var time)
            || !DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var decision)
            || decision.Kind != DateTimeKind.Utc || decision > now || now - decision > TimeSpan.FromSeconds(3)
            || !candidate.Metadata.TryGetValue("atrToPriceRatio", out var atrText)
            || !decimal.TryParse(atrText, NumberStyles.Float, CultureInfo.InvariantCulture, out var atr) || atr < 0)
            throw new InvalidOperationException("FX model requires a fresh long candidate and its exact decision-time price features.");
        var exit = exits.Value;
        if (instrument.MaximumHoldingSeconds != c.Profile.HoldingSeconds || exit.Mode != c.Profile.Exit.Mode
            || exit.BreakEvenTriggerR != c.Profile.Exit.BreakEvenTriggerR || exit.BreakEvenOffsetR != c.Profile.Exit.BreakEvenOffsetR
            || exit.TrailingActivationR != c.Profile.Exit.TrailingActivationR || exit.TrailingAtrMultiplier != c.Profile.Exit.TrailingAtrMultiplier
            || exit.Mode == ExitStrategyMode.AtrTrailing && execution.Value.Profiles.Single(x => x.InstrumentId == instrument.InstrumentId).TrailingIntervalSeconds != c.Profile.IntervalSeconds)
            throw new InvalidOperationException("FX model exit policy differs from the configured paper lifecycle.");
        var quote = latest.Get(candidate.InstrumentId);
        if (quote?.Bid is not > 0 || quote.Ask is not > 0 || quote.Ask < quote.Bid || quote.BidTimeUtc == null || quote.AskTimeUtc != quote.BidTimeUtc
            || quote.BidTimeUtc > now || now - quote.BidTimeUtc > TimeSpan.FromSeconds(c.Costs.MaximumSnapshotAgeSeconds))
            throw new InvalidOperationException("Fresh paired FX bid/ask is required before model prediction.");
        var mid = (quote.Bid.Value + quote.Ask.Value) / 2m;
        var notional = mid * c.Profile.MaximumBaseUnits;
        var estimatedCosts = 2 * Math.Max(c.Costs.MinimumCommissionPerOrderUsd / notional * 10000m, c.Costs.CommissionPerOrderBps)
            + c.Costs.SafetyBufferBps;
        var prediction = await models.PredictAsync(new(candidate.InstrumentId, candidate.StrategyId, c.Feature, candidate.Confidence,
            (int)candidate.PatternType, (int)candidate.Direction, (int)candidate.Timeframe, 0, atr, estimatedCosts, decision)
            { PatternVersion = c.Pattern, ExecutionLabelVersion = c.Label }, ct);
        if (!prediction.Eligible || prediction.ExpectedGrossReturnBps is not > 0 || prediction.ExpectedGrossReturnBps <= estimatedCosts
            || prediction.HorizonSeconds != c.Profile.HoldingSeconds)
            throw new InvalidOperationException("Approved FX model did not pass the forecast/cost/horizon gate.");
        var stop = quote.Ask.Value - c.Profile.StopPips * c.Costs.PipSize;
        var target = quote.Ask.Value + c.Profile.TargetPips * c.Costs.PipSize;
        var modelIdentity = $"{ForexResearchContract.Model}:{prediction.ModelId}:{prediction.ModelVersion}";
        var original = candidate.Context;
        var context = new PipelineContext(original.CorrelationId, original.SignalId, original.InstrumentId, original.StrategyId,
            original.MarketDataVersion, original.FeatureVersion, original.PatternVersion, original.StrategyVersion + "|" + modelIdentity + "|" + c.Label);
        var metadata = candidate.Metadata.ToDictionary(x => x.Key, x => x.Value);
        metadata["numericalModel"] = modelIdentity; metadata["executionLabelVersion"] = c.Label;
        metadata["modelGrossReturnBps"] = prediction.ExpectedGrossReturnBps.Value.ToString(CultureInfo.InvariantCulture);
        metadata["modelResidualCostsBps"] = estimatedCosts.ToString(CultureInfo.InvariantCulture);
        var approvedPattern = new PatternCandidate(candidate.PatternType, candidate.Symbol, candidate.Timeframe, candidate.DetectedAtUtc,
            candidate.Confidence, candidate.RelevantPriceLevels, metadata, context, candidate.Direction, candidate.HardConditions, candidate.ScoreComponents, candidate.EvaluationReasons);
        return new()
        {
            Pattern = approvedPattern, Snapshot = new() { Symbol = candidate.Symbol, CreatedAtUtc = now, CurrentPrice = mid, Spread = quote.Ask - quote.Bid },
            AiAnalysis = new() { Action = AiMarketActions.Buy, Confidence = prediction.Probability, PatternQuality = candidate.Confidence,
                // Execution-return prediction is deliberately not inflated by adding back spread/slippage.
                ExpectedMovePercent = prediction.ExpectedGrossReturnBps.Value / 100m, AnalyzedAtUtc = now,
                Model = modelIdentity, Reason = prediction.Reason,
                EntryMin = quote.Ask.Value, EntryMax = quote.Ask.Value, InvalidationPrice = stop },
            StrategyDecision = new() { Symbol = candidate.Symbol, Context = context, Approved = true, EvaluatedAtUtc = now,
                EntryMin = quote.Ask.Value, EntryMax = quote.Ask.Value, StopPrice = stop, TakeProfitPrice = target,
                ExpectedRewardRiskRatio = c.Profile.TargetPips / c.Profile.StopPips,
                TradeSignal = new(candidate.Symbol, candidate.Timeframe, TradeAction.Enter, prediction.Probability, c.Profile.MaximumBaseUnits, now) },
            ApprovedAtUtc = now
        };
    }
}
