using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class MarketDataStreamStateRecord
    {
        public long Id { get; set; }
        public string StreamKey { get; set; } = string.Empty;
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public MarketDataEventKind Kind { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string LastEventId { get; set; } = string.Empty;
        public DateTime? LastEventTimeUtc { get; set; }
        public DateTime? LastReceivedTimeUtc { get; set; }
        public long? LastSequence { get; set; }
        public MarketDataQualityStatus Status { get; set; }
        public string StatusReason { get; set; } = string.Empty;
        public bool IsHealthy { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int Version { get; set; }
    }

    public sealed class MarketDataQualityIncidentRecord
    {
        public long Id { get; set; }
        public string EventId { get; set; } = string.Empty;
        public string StreamKey { get; set; } = string.Empty;
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public MarketDataEventKind Kind { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTime EventTimeUtc { get; set; }
        public DateTime ReceivedTimeUtc { get; set; }
        public long? Sequence { get; set; }
        public MarketDataQualityStatus Status { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime RecordedAtUtc { get; set; }
    }
}
