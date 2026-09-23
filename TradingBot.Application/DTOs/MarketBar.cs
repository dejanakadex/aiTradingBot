using System;

namespace TradingBot.Application.DTOs
{
    public sealed class MarketBar
    {
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Timeframe { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; }
        public DateTime? ReceivedTimeUtc { get; init; }
        public string Source { get; init; } = string.Empty;
        public long? Sequence { get; init; }
        public bool IsFinal { get; init; } = true;
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public decimal Volume { get; init; }
    }
}
