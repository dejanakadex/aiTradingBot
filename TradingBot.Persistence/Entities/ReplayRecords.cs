using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class ReplayRunRecord
    {
        public Guid Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }
        public decimal SpeedMultiplier { get; set; }
        public ReplayRunStatus Status { get; set; }
        public string InputSha256 { get; set; } = string.Empty;
        public int InputEventCount { get; set; }
        public int ProcessedEventCount { get; set; }
        public int SignalCount { get; set; }
        public string CheckpointEventId { get; set; } = string.Empty;
        public DateTime? CheckpointEventTimeUtc { get; set; }
        public DateTime? CheckpointReceivedTimeUtc { get; set; }
        public string OutputSha256 { get; set; } = string.Empty;
        public string MarketDataVersion { get; set; } = string.Empty;
        public string FeatureVersion { get; set; } = string.Empty;
        public string PatternVersion { get; set; } = string.Empty;
        public string StrategyVersion { get; set; } = string.Empty;
        public string LastError { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public int Version { get; set; }
    }

    public sealed class ReplaySignalRecord
    {
        public long Id { get; set; }
        public Guid ReplayRunId { get; set; }
        public Guid SignalId { get; set; }
        public string SourceEventId { get; set; } = string.Empty;
        public PatternType PatternType { get; set; }
        public TradeDirection Direction { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public Timeframe Timeframe { get; set; }
        public DateTime DetectedAtUtc { get; set; }
        public decimal Confidence { get; set; }
        public string RelevantPriceLevelsJson { get; set; } = string.Empty;
        public string MetadataJson { get; set; } = string.Empty;
        public string FeaturesJson { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }
}
