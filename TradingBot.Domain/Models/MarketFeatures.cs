using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class MarketFeatures
    {
        public string FeatureVersion { get; init; } = string.Empty;
        public string InstrumentId { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; }
        public DateTime AsOfUtc { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public Timeframe Timeframe { get; init; }
        public int SampleCount { get; init; }

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
        public decimal? AtrToPriceRatio { get; init; }
        public decimal? RealizedVolatilityPercent { get; init; }
        public decimal? MomentumPercent { get; init; }
        public decimal? MeanReversionZScore { get; init; }
        public decimal? EmaSeparationPercent { get; init; }
        public decimal? EmaSeparationAtrRatio { get; init; }
        public decimal? DollarVolume { get; init; }
        public decimal? AverageDollarVolume { get; init; }
        public decimal? NormalizedLiquidity { get; init; }
        public decimal? Spread { get; init; }
        public decimal? SpreadBps { get; init; }
        public decimal? SpreadToAtrRatio { get; init; }
        public long? QuoteAgeMilliseconds { get; init; }
        public MarketRegime Regime { get; init; } = MarketRegime.Unknown;
        public string RegimeReason { get; init; } = string.Empty;
    }
}
