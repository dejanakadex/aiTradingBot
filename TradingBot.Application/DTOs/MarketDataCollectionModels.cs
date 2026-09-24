using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record MarketDataCollectionStreamSnapshot
    {
        public string StreamKey { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Timeframe { get; init; } = string.Empty;
        public MarketDataCollectionStatus Status { get; init; }
        public string StatusReason { get; init; } = string.Empty;
        public bool BrokerConnected { get; init; }
        public bool Subscribed { get; init; }
        public DateTime? LastHeartbeatUtc { get; init; }
        public DateTime? LastEventTimeUtc { get; init; }
        public long? ReceiveLagMilliseconds { get; init; }
        public int HeartbeatTimeoutSeconds { get; init; }
        public int ConsecutiveFailures { get; init; }
        public int ReconnectCount { get; init; }
        public int GapFillCount { get; init; }
        public DateTime? LastGapFillStartUtc { get; init; }
        public DateTime? LastGapFillEndUtc { get; init; }
        public int LastGapFillBars { get; init; }
        public string LastError { get; init; } = string.Empty;
        public DateTime UpdatedAtUtc { get; init; }
    }

    public sealed record HistoricalGapFillResult(
        bool Success,
        string InstrumentId,
        string Timeframe,
        DateTime StartUtc,
        DateTime EndUtc,
        int BarsReceived,
        int BarsInserted,
        int DuplicateBars,
        string Error);
}
