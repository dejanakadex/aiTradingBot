using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record ResearchCandidateSnapshot(
        long Id,
        string RecordKey,
        string CandidateKey,
        Guid? ReplayRunId,
        Guid? SignalId,
        string SourceEventId,
        string InstrumentId,
        string Symbol,
        string StrategyId,
        PatternType PatternType,
        TradeDirection Direction,
        Timeframe Timeframe,
        DateTime EvaluatedAtUtc,
        decimal ReferencePrice,
        decimal Confidence,
        ResearchCandidateOutcome Outcome,
        string DecisionStage,
        string FeatureVersion,
        string PatternVersion,
        string LabelVersion,
        string MarketRegime,
        decimal? NormalizedLiquidity,
        decimal? NormalizedVolatility,
        string ReasonsJson,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);

    public sealed record CandidateLabelSnapshot(
        long Id,
        long ResearchCandidateId,
        int HorizonSeconds,
        CandidateLabelStatus Status,
        TargetStopOutcome TargetStopOutcome,
        DateTime WindowStartUtc,
        DateTime WindowEndUtc,
        int ObservationCount,
        decimal EntryPrice,
        decimal? ExitPrice,
        decimal? MaximumFavorableExcursionBps,
        decimal? MaximumAdverseExcursionBps,
        decimal? GrossReturnBps,
        decimal? EstimatedCostBps,
        decimal? NetReturnBps,
        decimal? ObservedSpreadBps,
        DateTime? MaximumEventTimeUtc,
        string ReasonsJson,
        string LabelVersion,
        DateTime? CalculatedAtUtc);
}
