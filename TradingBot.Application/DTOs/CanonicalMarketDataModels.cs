using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record CanonicalMarketDataEvent
    {
        public string EventId { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public MarketDataEventKind Kind { get; init; }
        public DateTime EventTimeUtc { get; init; }
        public DateTime ReceivedTimeUtc { get; init; }
        public string Source { get; init; } = string.Empty;
        public long? Sequence { get; init; }
        public bool IsFinal { get; init; } = true;
        public string Timeframe { get; init; } = string.Empty;
        public decimal? Price { get; init; }
        public decimal? Size { get; init; }
        public decimal? Open { get; init; }
        public decimal? High { get; init; }
        public decimal? Low { get; init; }
        public decimal? Close { get; init; }
        public decimal? Volume { get; init; }

        public string StreamKey => $"{InstrumentId}|{Kind}|{Timeframe}".ToUpperInvariant();
    }

    public sealed record MarketDataQualityAssessment(
        MarketDataQualityStatus Status,
        string Reason,
        bool CanPersist,
        bool CanTriggerTrading)
    {
        public bool IsHealthy => Status == MarketDataQualityStatus.Healthy;
    }

    public sealed record MarketDataStreamSnapshot
    {
        public string StreamKey { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public MarketDataEventKind Kind { get; init; }
        public string Timeframe { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public DateTime? LastEventTimeUtc { get; init; }
        public DateTime? LastReceivedTimeUtc { get; init; }
        public long? LastSequence { get; init; }
        public MarketDataQualityStatus Status { get; init; }
        public string StatusReason { get; init; } = string.Empty;
        public bool IsHealthy { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
        public int Version { get; init; }
    }

    public sealed record MarketDataQualityIncidentSnapshot
    {
        public long Id { get; init; }
        public string EventId { get; init; } = string.Empty;
        public string StreamKey { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public MarketDataEventKind Kind { get; init; }
        public string Timeframe { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public DateTime EventTimeUtc { get; init; }
        public DateTime ReceivedTimeUtc { get; init; }
        public long? Sequence { get; init; }
        public MarketDataQualityStatus Status { get; init; }
        public string Reason { get; init; } = string.Empty;
        public DateTime RecordedAtUtc { get; init; }
    }

    public sealed record ShortIntervalBarSnapshot
    {
        public int IntervalSeconds { get; init; }
        public DateTime WindowStartUtc { get; init; }
        public DateTime LastEventTimeUtc { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public decimal Volume { get; init; }
        public int TradeCount { get; init; }
    }

    public sealed record LatestMarketDataSnapshot
    {
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public decimal? Bid { get; init; }
        public decimal? Ask { get; init; }
        public decimal? LastTrade { get; init; }
        public decimal? Spread => Bid.HasValue && Ask.HasValue ? Ask.Value - Bid.Value : null;
        public DateTime? BidTimeUtc { get; init; }
        public DateTime? AskTimeUtc { get; init; }
        public DateTime? LastTradeTimeUtc { get; init; }
        public DateTime? AsOfUtc { get; init; }
        public IReadOnlyList<ShortIntervalBarSnapshot> ShortAggregates { get; init; } = Array.Empty<ShortIntervalBarSnapshot>();
    }
}
