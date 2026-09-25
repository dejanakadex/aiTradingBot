using TradingBot.Domain.Models;

namespace TradingBot.Application.DTOs
{
    public sealed record ResearchEvaluationRequest(
        int HorizonSeconds = 60,
        DateTime? FromUtc = null,
        DateTime? ToUtc = null,
        Guid? ReplayRunId = null,
        string? InstrumentId = null,
        string? StrategyId = null);

    public sealed record ResearchEvaluationRunSnapshot(
        Guid Id,
        string RunKey,
        Guid? ReplayRunId,
        string InstrumentId,
        string StrategyId,
        int HorizonSeconds,
        DateTime FromUtc,
        DateTime ToUtc,
        DateTime HoldoutStartUtc,
        int SourceCandidateCount,
        int EligibleCandidateCount,
        int WalkForwardFoldCount,
        decimal SelectedConfidenceThreshold,
        string SelectionReason,
        string EvaluationVersion,
        string FeatureVersion,
        string PatternVersion,
        string LabelVersion,
        string InputSha256,
        string OutputSha256,
        EvaluationMetrics PreHoldoutMetrics,
        EvaluationMetrics WalkForwardMetrics,
        EvaluationMetrics HoldoutMetrics,
        IReadOnlyList<ThresholdEvaluation> PreHoldoutThresholds,
        IReadOnlyList<WalkForwardFoldResult> Folds,
        IReadOnlyList<CostSensitivityResult> CostSensitivity,
        IReadOnlyList<EvaluationSegmentResult> Segments,
        DateTime CreatedAtUtc,
        DateTime CompletedAtUtc);
}
