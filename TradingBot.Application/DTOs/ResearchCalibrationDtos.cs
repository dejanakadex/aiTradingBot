using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.DTOs
{
    public sealed record ResearchCalibrationRequest(Guid EvaluationRunId);

    public sealed record ResearchCalibrationDecisionRequest(
        ResearchCalibrationDecision Decision,
        string Reviewer,
        string Reason,
        string ExpectedOutputSha256);

    public sealed record ResearchCalibrationApprovalSnapshot(
        long Id,
        int Revision,
        string Action,
        string Reviewer,
        string Reason,
        string ExpectedOutputSha256,
        Guid? RelatedProfileId,
        DateTime CreatedAtUtc);

    public sealed record ResearchCalibrationProfileSnapshot(
        Guid Id,
        string ProfileKey,
        int ModelVersion,
        int Revision,
        ResearchCalibrationStatus Status,
        Guid EvaluationRunId,
        Guid? ReplayRunId,
        string InstrumentId,
        string StrategyId,
        int HorizonSeconds,
        string CalibrationVersion,
        string EvaluationVersion,
        string FeatureVersion,
        string PatternVersion,
        string LabelVersion,
        decimal StableConfidenceThreshold,
        decimal MinimumCalibratedProbability,
        decimal AverageWinBps,
        decimal AverageLossBps,
        decimal AverageEstimatedCostBps,
        ThresholdStabilityResult ThresholdStability,
        IReadOnlyList<ProbabilityCalibrationPoint> CalibrationPoints,
        ProbabilityCalibrationMetrics WalkForwardRawMetrics,
        ProbabilityCalibrationMetrics WalkForwardCalibratedMetrics,
        ProbabilityCalibrationMetrics HoldoutRawMetrics,
        ProbabilityCalibrationMetrics HoldoutCalibratedMetrics,
        CalibrationBaselineComparison BaselineComparison,
        bool ApprovalReady,
        string InputSha256,
        string OutputSha256,
        DateTime CreatedAtUtc,
        DateTime? DecidedAtUtc,
        IReadOnlyList<ResearchCalibrationApprovalSnapshot> ApprovalHistory);

    public sealed record OpportunityRankingCandidate(
        string CandidateKey,
        string InstrumentId,
        string StrategyId,
        decimal RawConfidence,
        decimal EstimatedCostBps,
        DateTime ObservedAtUtc);

    public sealed record OpportunityRankingRequest(
        string FeatureVersion,
        string PatternVersion,
        string LabelVersion,
        IReadOnlyList<OpportunityRankingCandidate> Candidates);

    public sealed record RankedOpportunity(
        int Rank,
        string CandidateKey,
        string InstrumentId,
        string StrategyId,
        decimal RawConfidence,
        decimal CalibratedProbability,
        decimal ExpectedNetReturnBps,
        bool Eligible,
        IReadOnlyList<string> Reasons,
        DateTime ObservedAtUtc);

    public sealed record OpportunityRankingResult(
        Guid CalibrationProfileId,
        int ModelVersion,
        string CalibrationVersion,
        string ProfileOutputSha256,
        IReadOnlyList<RankedOpportunity> Opportunities);
}
