using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record NumericalModelTrainingRequest(Guid EvaluationRunId);
    public sealed record NumericalModelDecisionRequest(bool Approve, string Reviewer, string Reason, string ExpectedOutputSha256);
    public sealed record NumericalModelRollbackRequest(string Reviewer, string Reason, string ExpectedOutputSha256);
    public sealed record NumericalModelDecisionSnapshot(long Id, Guid ModelId, string InstrumentId, string StrategyId,
        NumericalModelStatus StatusBefore, NumericalModelStatus StatusAfter, string Action, string Reviewer,
        string Reason, string ReviewedOutputSha256, DateTime DecidedAtUtc);
    public sealed record NumericalModelMetrics(int SampleCount, int SelectedCount, decimal WinRate, decimal ExpectancyBps, decimal NetProfitBps, decimal MaximumDrawdownBps)
    {
        public decimal? ForecastMeanAbsoluteErrorBps { get; init; }
    }
    public sealed record NumericalModelSnapshot(
        Guid Id, int ModelVersion, NumericalModelStatus Status, Guid EvaluationRunId, string InstrumentId, string StrategyId,
        int HorizonSeconds, string Algorithm, string ModelVersionTag, string FeatureVersion, string PatternVersion, string LabelVersion,
        decimal ProbabilityThreshold, NumericalModelMetrics BaselineWalkForward, NumericalModelMetrics CandidateWalkForward,
        NumericalModelMetrics BaselineHoldout, NumericalModelMetrics CandidateHoldout, bool ApprovalReady,
        IReadOnlyList<string> ApprovalReasons, string InputSha256, string OutputSha256, DateTime CreatedAtUtc, DateTime? DecidedAtUtc);
    public sealed record NumericalModelPredictionRequest(
        string InstrumentId, string StrategyId, string FeatureVersion, decimal PatternConfidence, int PatternType, int Direction,
        int Timeframe, decimal NormalizedLiquidity, decimal NormalizedVolatility, decimal EstimatedCostBps, DateTime ObservedAtUtc)
    {
        public string? PatternVersion { get; init; }
        public string? ExecutionLabelVersion { get; init; }
    }
    public sealed record NumericalModelPrediction(Guid ModelId, int ModelVersion, decimal Probability, decimal Threshold, decimal HistoricalSelectedNetReturnBps, int HorizonSeconds, bool Eligible, string Reason)
    {
        public decimal? ExpectedGrossReturnBps { get; init; }
    }
}
