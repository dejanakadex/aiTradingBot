using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed record ResearchEvaluationObservation(
        long CandidateId,
        string CandidateKey,
        string InstrumentId,
        string StrategyId,
        PatternType PatternType,
        TradeDirection Direction,
        Timeframe Timeframe,
        DateTime EvaluatedAtUtc,
        DateTime LabelWindowEndUtc,
        decimal Confidence,
        ResearchCandidateOutcome Outcome,
        string MarketRegime,
        decimal? NormalizedLiquidity,
        decimal GrossReturnBps,
        decimal EstimatedCostBps,
        decimal NetReturnBps,
        decimal MaximumFavorableExcursionBps,
        decimal MaximumAdverseExcursionBps,
        TargetStopOutcome TargetStopOutcome,
        string FeatureVersion,
        string PatternVersion,
        string LabelVersion);

    public sealed record EvaluationMetrics(
        int SampleCount,
        int WinCount,
        int LossCount,
        int BreakEvenCount,
        decimal WinRate,
        decimal ExpectancyBps,
        decimal NetProfitBps,
        decimal GrossProfitBps,
        decimal GrossLossBps,
        decimal? ProfitFactor,
        decimal MaximumDrawdownBps,
        decimal AverageMfeBps,
        decimal AverageMaeBps,
        decimal TargetFirstRate,
        decimal StopFirstRate,
        decimal AmbiguousRate);

    public sealed record ThresholdEvaluation(decimal ConfidenceThreshold, EvaluationMetrics Metrics);

    public sealed record WalkForwardFoldResult(
        int Fold,
        DateTime TrainingFromUtc,
        DateTime TrainingToUtc,
        DateTime ValidationFromUtc,
        DateTime ValidationToUtc,
        int TrainingSourceCount,
        int ValidationSourceCount,
        decimal? SelectedConfidenceThreshold,
        string SelectionReason,
        EvaluationMetrics? TrainingMetrics,
        EvaluationMetrics? ValidationMetrics);

    public sealed record CostSensitivityResult(decimal CostMultiplier, EvaluationMetrics Metrics);

    public sealed record EvaluationSegmentResult(string Dimension, string Value, EvaluationMetrics Metrics);

    public sealed class ResearchEvaluationCalculation
    {
        public DateTime FromUtc { get; init; }
        public DateTime ToUtc { get; init; }
        public DateTime HoldoutStartUtc { get; init; }
        public int SourceCandidateCount { get; init; }
        public int EligibleCandidateCount { get; init; }
        public string FeatureVersion { get; init; } = string.Empty;
        public string PatternVersion { get; init; } = string.Empty;
        public string LabelVersion { get; init; } = string.Empty;
        public decimal SelectedConfidenceThreshold { get; init; }
        public string SelectionReason { get; init; } = string.Empty;
        public EvaluationMetrics PreHoldoutMetrics { get; init; } = EmptyMetrics;
        public EvaluationMetrics WalkForwardMetrics { get; init; } = EmptyMetrics;
        public EvaluationMetrics HoldoutMetrics { get; init; } = EmptyMetrics;
        public IReadOnlyList<ThresholdEvaluation> PreHoldoutThresholds { get; init; } = Array.Empty<ThresholdEvaluation>();
        public IReadOnlyList<WalkForwardFoldResult> Folds { get; init; } = Array.Empty<WalkForwardFoldResult>();
        public IReadOnlyList<CostSensitivityResult> CostSensitivity { get; init; } = Array.Empty<CostSensitivityResult>();
        public IReadOnlyList<EvaluationSegmentResult> Segments { get; init; } = Array.Empty<EvaluationSegmentResult>();
        public IReadOnlyList<long> WalkForwardCandidateIds { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> HoldoutCandidateIds { get; init; } = Array.Empty<long>();

        public static EvaluationMetrics EmptyMetrics { get; } = new(0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, null, 0m, 0m, 0m, 0m, 0m, 0m);
    }
}
