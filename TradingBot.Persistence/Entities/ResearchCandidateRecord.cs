using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class ResearchCandidateRecord
    {
        public long Id { get; set; }
        public string RecordKey { get; set; } = string.Empty;
        public string CandidateKey { get; set; } = string.Empty;
        public Guid? ReplayRunId { get; set; }
        public Guid? SignalId { get; set; }
        public string SourceEventId { get; set; } = string.Empty;
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public PatternType PatternType { get; set; }
        public TradeDirection Direction { get; set; }
        public Timeframe Timeframe { get; set; }
        public DateTime EvaluatedAtUtc { get; set; }
        public decimal ReferencePrice { get; set; }
        public decimal Confidence { get; set; }
        public ResearchCandidateOutcome Outcome { get; set; }
        public string DecisionStage { get; set; } = string.Empty;
        public string FeatureVersion { get; set; } = string.Empty;
        public string PatternVersion { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public string HardConditionsJson { get; set; } = string.Empty;
        public string ScoreComponentsJson { get; set; } = string.Empty;
        public string ReasonsJson { get; set; } = string.Empty;
        public string MetadataJson { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public ICollection<CandidateLabelRecord> Labels { get; set; } = new List<CandidateLabelRecord>();
    }

    public sealed class CandidateLabelRecord
    {
        public long Id { get; set; }
        public long ResearchCandidateId { get; set; }
        public ResearchCandidateRecord ResearchCandidate { get; set; } = null!;
        public int HorizonSeconds { get; set; }
        public CandidateLabelStatus Status { get; set; }
        public TargetStopOutcome TargetStopOutcome { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public DateTime WindowEndUtc { get; set; }
        public int ObservationCount { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal? ExitPrice { get; set; }
        public decimal? MaximumFavorableExcursionBps { get; set; }
        public decimal? MaximumAdverseExcursionBps { get; set; }
        public decimal? GrossReturnBps { get; set; }
        public decimal? EstimatedCostBps { get; set; }
        public decimal? NetReturnBps { get; set; }
        public decimal? ObservedSpreadBps { get; set; }
        public string FirstTargetEventId { get; set; } = string.Empty;
        public string FirstStopEventId { get; set; } = string.Empty;
        public DateTime? MaximumEventTimeUtc { get; set; }
        public string ReasonsJson { get; set; } = string.Empty;
        public string LabelVersion { get; set; } = string.Empty;
        public DateTime? CalculatedAtUtc { get; set; }
    }
}
