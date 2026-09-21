using System;

namespace TradingBot.Domain.Models
{
    public sealed class MarketFeatures
    {
        public DateTime TimestampUtc { get; init; }
        public string Symbol { get; init; } = string.Empty;

        public decimal? EmaShort { get; init; }
        public decimal? EmaLong { get; init; }
        public decimal? Rsi { get; init; }
        public decimal? Atr { get; init; }
        public decimal? Vwap { get; init; }
        public decimal? VolumeAverage { get; init; }
        public decimal? VolumeRatio { get; init; }
        public decimal? PriceChangePercent { get; init; }
        public decimal? DistanceFromVwapPercent { get; init; }
        public decimal? RecentHigh { get; init; }
        public decimal? RecentLow { get; init; }
        public int? TrendDirection { get; init; } // -1 down, 0 flat, 1 up
        public decimal? Volatility { get; init; }
    }
}
