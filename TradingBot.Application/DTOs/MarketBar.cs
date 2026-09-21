using System;

namespace TradingBot.Application.DTOs
{
    public sealed class MarketBar
    {
        public string Symbol { get; init; } = string.Empty;
        public string Timeframe { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; }
        public decimal Open { get; init; }
        public decimal High { get; init; }
        public decimal Low { get; init; }
        public decimal Close { get; init; }
        public decimal Volume { get; init; }
    }
}
