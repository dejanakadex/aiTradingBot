using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class NumericalModelRecord
    {
        public Guid Id { get; set; }
        public int ModelVersion { get; set; }
        public NumericalModelStatus Status { get; set; }
        public Guid EvaluationRunId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public int HorizonSeconds { get; set; }
        public string Algorithm { get; set; } = string.Empty;
        public string ModelVersionTag { get; set; } = string.Empty;
        public string FeatureVersion { get; set; } = string.Empty;
        public string PatternVersion { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public decimal ProbabilityThreshold { get; set; }
        public string BaselineWalkForwardJson { get; set; } = string.Empty;
        public string CandidateWalkForwardJson { get; set; } = string.Empty;
        public string BaselineHoldoutJson { get; set; } = string.Empty;
        public string CandidateHoldoutJson { get; set; } = string.Empty;
        public bool ApprovalReady { get; set; }
        public string ApprovalReasonsJson { get; set; } = "[]";
        public byte[] ModelArtifact { get; set; } = Array.Empty<byte>();
        public string InputSha256 { get; set; } = string.Empty;
        public string OutputSha256 { get; set; } = string.Empty;
        public string Reviewer { get; set; } = string.Empty;
        public string DecisionReason { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? DecidedAtUtc { get; set; }
    }
}
