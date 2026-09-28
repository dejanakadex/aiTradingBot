using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class ResearchCalibrationProfileRecord
    {
        public Guid Id { get; set; }
        public string ProfileKey { get; set; } = string.Empty;
        public int ModelVersion { get; set; }
        public int Revision { get; set; }
        public ResearchCalibrationStatus Status { get; set; }
        public Guid EvaluationRunId { get; set; }
        public Guid? ReplayRunId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public int HorizonSeconds { get; set; }
        public string CalibrationVersion { get; set; } = string.Empty;
        public string EvaluationVersion { get; set; } = string.Empty;
        public string FeatureVersion { get; set; } = string.Empty;
        public string PatternVersion { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public decimal StableConfidenceThreshold { get; set; }
        public decimal MinimumCalibratedProbability { get; set; }
        public decimal AverageWinBps { get; set; }
        public decimal AverageLossBps { get; set; }
        public decimal AverageEstimatedCostBps { get; set; }
        public string ThresholdStabilityJson { get; set; } = string.Empty;
        public string CalibrationPointsJson { get; set; } = string.Empty;
        public string WalkForwardRawMetricsJson { get; set; } = string.Empty;
        public string WalkForwardCalibratedMetricsJson { get; set; } = string.Empty;
        public string HoldoutRawMetricsJson { get; set; } = string.Empty;
        public string HoldoutCalibratedMetricsJson { get; set; } = string.Empty;
        public string BaselineComparisonJson { get; set; } = string.Empty;
        public string InputSha256 { get; set; } = string.Empty;
        public string OutputSha256 { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? DecidedAtUtc { get; set; }
        public List<ResearchCalibrationApprovalRecord> ApprovalHistory { get; set; } = new();
    }
}
