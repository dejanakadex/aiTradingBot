using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class MarketSnapshot
    {
        public string Symbol { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public decimal? CurrentPrice { get; init; }
        public decimal? Spread { get; init; }
        public MarketTimeframeSnapshot OneMinute { get; init; } = MarketTimeframeSnapshot.Empty(Timeframe.OneMinute, "Entry timing");
        public MarketTimeframeSnapshot FiveMinutes { get; init; } = MarketTimeframeSnapshot.Empty(Timeframe.FiveMinutes, "Trading setup / pullback context");
        public MarketTimeframeSnapshot FifteenMinutes { get; init; } = MarketTimeframeSnapshot.Empty(Timeframe.FifteenMinutes, "Broader market direction");
    }

    public sealed class MarketTimeframeSnapshot
    {
        public Timeframe Timeframe { get; init; }
        public string Interpretation { get; init; } = string.Empty;
        public IReadOnlyList<Candle> RecentCandles { get; init; } = Array.Empty<Candle>();
        public MarketFeatures? Features { get; init; }
        public IReadOnlyList<PatternCandidate> DetectedPatterns { get; init; } = Array.Empty<PatternCandidate>();
        public IReadOnlyList<SupportResistanceCandidate> SupportResistanceCandidates { get; init; } = Array.Empty<SupportResistanceCandidate>();
        public TrendInformation Trend { get; init; } = new();
        public VolumeContext Volume { get; init; } = new();
        public VolatilityContext Volatility { get; init; } = new();

        public static MarketTimeframeSnapshot Empty(Timeframe timeframe, string interpretation)
        {
            return new MarketTimeframeSnapshot
            {
                Timeframe = timeframe,
                Interpretation = interpretation
            };
        }
    }

    public sealed class SupportResistanceCandidate
    {
        public decimal Price { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
    }

    public sealed class TrendInformation
    {
        public int? Direction { get; init; }
        public string Label { get; init; } = "Unknown";
        public decimal? EmaShort { get; init; }
        public decimal? EmaLong { get; init; }
        public decimal? PriceChangePercent { get; init; }
    }

    public sealed class VolumeContext
    {
        public decimal? LatestVolume { get; init; }
        public decimal? AverageVolume { get; init; }
        public decimal? VolumeRatio { get; init; }
    }

    public sealed class VolatilityContext
    {
        public decimal? Atr { get; init; }
        public decimal? Volatility { get; init; }
        public decimal? RecentHigh { get; init; }
        public decimal? RecentLow { get; init; }
    }
}
