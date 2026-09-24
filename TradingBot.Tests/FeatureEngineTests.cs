using System;
using System.Collections.Generic;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class FeatureEngineTests
    {
        [Fact]
        public void Ema_Computation_Period3_KnownSequence()
        {
            // closes: 10,11,12,13 -> EMA period 3 -> alpha=0.5, initial SMA=(10+11+12)/3=11 -> next EMA=(13-11)*0.5+11=12
            var candles = new List<Candle>
            {
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-3), 0,0,0,10,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2), 0,0,0,11,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1), 0,0,0,12,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 0,0,0,13,1),
            };

            var eng = new FeatureEngine(emaShort:3, emaLong:5, rsi:2, atr:2, volAvg:3);
            var f = eng.ComputeFeatures(candles);
            Assert.NotNull(f.EmaShort);
            Assert.Equal(12m, Math.Round(f.EmaShort.Value, 6));
        }

        [Fact]
        public void Rsi_Computation_Simple()
        {
            // close sequence: 1,2,3 -> period 2 -> gains 1,1 -> avgGain=1 avgLoss=0 -> RSI=100
            var candles = new List<Candle>
            {
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2),0,0,0,1,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),0,0,0,2,1),
                new Candle("X", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,0,0,0,3,1),
            };
            var eng = new FeatureEngine(emaShort:3, emaLong:5, rsi:2, atr:2, volAvg:3);
            var f = eng.ComputeFeatures(candles);
            Assert.Equal(100m, f.Rsi);
        }

        [Fact]
        public void Vwap_Computation_Known()
        {
            // two candles: typical prices 10 & 20, volumes 1 & 1 -> VWAP=(10+20)/2=15
            var candles = new List<Candle>
            {
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),9,11,9,10,1),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,19,21,19,20,1),
            };

            var eng = new FeatureEngine();
            var f = eng.ComputeFeatures(candles);
            Assert.NotNull(f.Vwap);
            Assert.Equal(15m, Math.Round(f.Vwap.Value, 6));
        }

        [Fact]
        public void VolumeRatio_Computation()
        {
            // last volume 2, avg previous 2 over period 2 => ratio 1
            var candles = new List<Candle>
            {
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-2),0,0,0,10,2),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow.AddMinutes(-1),0,0,0,11,2),
                new Candle("S", Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow,0,0,0,12,2),
            };
            var eng = new FeatureEngine(volAvg:2);
            var f = eng.ComputeFeatures(candles);
            Assert.Equal(1m, f.VolumeRatio);
        }

        [Fact]
        public void CanonicalInput_IsOrderIndependentAndExcludesFutureCandlesAndQuotes()
        {
            var asOfUtc = new DateTime(2026, 9, 24, 14, 5, 0, DateTimeKind.Utc);
            var candles = BuildSeries(asOfUtc.AddMinutes(-5), 6).ToList();
            candles.Add(new Candle("SPY", Timeframe.OneMinute, asOfUtc.AddMinutes(1), 999m, 1001m, 998m, 1000m, 99_999m, "US-STK-SPY-SMART"));
            var engine = CreateCanonicalEngine();

            var chronological = engine.ComputeFeatures(new CanonicalFeatureInput
            {
                Candles = candles,
                AsOfUtc = asOfUtc,
                Bid = 105.00m,
                BidTimeUtc = asOfUtc.AddSeconds(1),
                Ask = 105.04m,
                AskTimeUtc = asOfUtc.AddSeconds(1)
            });
            var reversed = engine.ComputeFeatures(new CanonicalFeatureInput
            {
                Candles = candles.AsEnumerable().Reverse().ToArray(),
                AsOfUtc = asOfUtc,
                Bid = 105.00m,
                BidTimeUtc = asOfUtc.AddSeconds(1),
                Ask = 105.04m,
                AskTimeUtc = asOfUtc.AddSeconds(1)
            });

            Assert.Equal(6, chronological.SampleCount);
            Assert.Equal(asOfUtc, chronological.TimestampUtc);
            Assert.Equal(chronological.FeatureVersion, reversed.FeatureVersion);
            Assert.Equal(chronological.EmaShort, reversed.EmaShort);
            Assert.Equal(chronological.Vwap, reversed.Vwap);
            Assert.Equal(chronological.MomentumPercent, reversed.MomentumPercent);
            Assert.Equal(chronological.MeanReversionZScore, reversed.MeanReversionZScore);
            Assert.Null(chronological.Spread);
            Assert.Null(reversed.Spread);
        }

        [Fact]
        public void CanonicalInput_ComputesSpreadMomentumMeanReversionAndNormalizedMetrics()
        {
            var asOfUtc = new DateTime(2026, 9, 24, 14, 5, 0, DateTimeKind.Utc);
            var engine = CreateCanonicalEngine();
            var features = engine.ComputeFeatures(new CanonicalFeatureInput
            {
                Candles = BuildSeries(asOfUtc.AddMinutes(-5), 6),
                AsOfUtc = asOfUtc,
                Bid = 104.98m,
                BidTimeUtc = asOfUtc.AddSeconds(-2),
                Ask = 105.02m,
                AskTimeUtc = asOfUtc.AddSeconds(-1),
                LastTrade = 105m,
                LastTradeTimeUtc = asOfUtc
            });

            Assert.Equal("US-STK-SPY-SMART", features.InstrumentId);
            Assert.Equal(Timeframe.OneMinute, features.Timeframe);
            Assert.StartsWith("features-v2+config-", features.FeatureVersion);
            Assert.Equal(0.04m, features.Spread);
            Assert.Equal(400m / 105m, features.SpreadBps);
            Assert.Equal(1000L, features.QuoteAgeMilliseconds);
            Assert.Equal(200m / 103m, features.MomentumPercent);
            Assert.NotNull(features.MeanReversionZScore);
            Assert.NotNull(features.AtrToPriceRatio);
            Assert.NotNull(features.RealizedVolatilityPercent);
            Assert.NotNull(features.DollarVolume);
            Assert.NotNull(features.AverageDollarVolume);
            Assert.NotNull(features.NormalizedLiquidity);
            Assert.NotEqual(MarketRegime.Unknown, features.Regime);
        }

        [Fact]
        public void SameAsOf_FromLiveBackfillAndReplayShapes_ProducesIdenticalCanonicalFeatures()
        {
            var asOfUtc = new DateTime(2026, 9, 24, 14, 10, 0, DateTimeKind.Utc);
            var canonical = BuildSeries(asOfUtc.AddMinutes(-10), 11).ToArray();
            var liveWindow = canonical.ToArray();
            var backfillWindow = canonical.Reverse().ToArray();
            var replayWindow = canonical.Concat(new[]
            {
                new Candle("SPY", Timeframe.OneMinute, asOfUtc.AddMinutes(1), 900m, 901m, 899m, 900m, 10m, "US-STK-SPY-SMART")
            }).ToArray();
            var engine = CreateCanonicalEngine();

            MarketFeatures Calculate(IReadOnlyList<Candle> input) => engine.ComputeFeatures(new CanonicalFeatureInput
            {
                Candles = input,
                AsOfUtc = asOfUtc,
                Spread = 0.02m,
                SpreadTimeUtc = asOfUtc
            });

            var live = Calculate(liveWindow);
            var backfill = Calculate(backfillWindow);
            var replay = Calculate(replayWindow);

            Assert.Equal(live, backfill, MarketFeatureComparer.Instance);
            Assert.Equal(live, replay, MarketFeatureComparer.Instance);
        }

        [Fact]
        public void FeatureVersionChangesWhenCanonicalConfigurationChanges()
        {
            var first = CreateCanonicalEngine();
            var second = new FeatureEngine(new CanonicalFeatureSettings
            {
                EmaShortPeriod = 2,
                EmaLongPeriod = 4,
                RsiPeriod = 2,
                AtrPeriod = 2,
                VolumeAveragePeriod = 3,
                MomentumLookbackCandles = 3,
                MeanReversionLookbackCandles = 3,
                MaximumQuoteAgeSeconds = 30
            });

            Assert.NotEqual(first.FeatureVersion, second.FeatureVersion);
        }

        private static FeatureEngine CreateCanonicalEngine() => new(new CanonicalFeatureSettings
        {
            EmaShortPeriod = 2,
            EmaLongPeriod = 4,
            RsiPeriod = 2,
            AtrPeriod = 2,
            VolumeAveragePeriod = 3,
            MomentumLookbackCandles = 2,
            MeanReversionLookbackCandles = 3,
            MaximumQuoteAgeSeconds = 30,
            TrendingEmaSeparationAtrRatio = 0.1m,
            VolatileAtrToPriceRatio = 0.5m
        });

        private static IReadOnlyList<Candle> BuildSeries(DateTime startUtc, int count) => Enumerable.Range(0, count)
            .Select(index =>
            {
                var close = 100m + index;
                return new Candle(
                    "SPY",
                    Timeframe.OneMinute,
                    startUtc.AddMinutes(index),
                    close - 0.25m,
                    close + 0.5m,
                    close - 0.5m,
                    close,
                    1_000m + index * 100m,
                    "US-STK-SPY-SMART",
                    startUtc.AddMinutes(index).AddMilliseconds(10),
                    "Test");
            })
            .ToArray();

        private sealed class MarketFeatureComparer : IEqualityComparer<MarketFeatures>
        {
            public static MarketFeatureComparer Instance { get; } = new();

            public bool Equals(MarketFeatures? left, MarketFeatures? right)
            {
                if (left == null || right == null) return left == right;
                return System.Text.Json.JsonSerializer.Serialize(left) == System.Text.Json.JsonSerializer.Serialize(right);
            }

            public int GetHashCode(MarketFeatures value) => System.Text.Json.JsonSerializer.Serialize(value).GetHashCode(StringComparison.Ordinal);
        }
    }
}
