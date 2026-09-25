namespace TradingBot.Persistence
{
    public sealed class ResearchEvaluationRunRecord
    {
        public Guid Id { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public Guid? ReplayRunId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public int HorizonSeconds { get; set; }
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public DateTime HoldoutStartUtc { get; set; }
        public int SourceCandidateCount { get; set; }
        public int EligibleCandidateCount { get; set; }
        public int WalkForwardFoldCount { get; set; }
        public decimal SelectedConfidenceThreshold { get; set; }
        public string SelectionReason { get; set; } = string.Empty;
        public string EvaluationVersion { get; set; } = string.Empty;
        public string FeatureVersion { get; set; } = string.Empty;
        public string PatternVersion { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public string InputSha256 { get; set; } = string.Empty;
        public string OutputSha256 { get; set; } = string.Empty;
        public string PreHoldoutMetricsJson { get; set; } = string.Empty;
        public string WalkForwardMetricsJson { get; set; } = string.Empty;
        public string HoldoutMetricsJson { get; set; } = string.Empty;
        public string PreHoldoutThresholdsJson { get; set; } = string.Empty;
        public string FoldsJson { get; set; } = string.Empty;
        public string CostSensitivityJson { get; set; } = string.Empty;
        public string SegmentsJson { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
    }
}
