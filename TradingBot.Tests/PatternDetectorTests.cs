using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class PatternDetectorTests
    {
        private Candle MakeC(string sym, decimal open, decimal high, decimal low, decimal close, decimal vol, DateTime ts)
        {
            return new Candle(sym, Domain.Enums.Timeframe.OneMinute, ts, open, high, low, close, vol);
        }

        [Fact]
        public void Detects_Hammer()
        {
            var opts = new PatternDetectorOptions();
            var det = new PatternDetector(opts, NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X", 10, 15, 5, 12, 100, now.AddMinutes(-1)),
                // hammer: small body near top, long lower wick
                MakeC("X", 12, 12.5m, 9, 12.2m, 50, now)
            };
            var found = det.Detect(candles);
            Assert.Contains(found, f => f.PatternType == Domain.Enums.PatternType.Hammer);
        }

        [Fact]
        public void Detects_BullishEngulfing()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X", 15,16,14,14.5m,100, now.AddMinutes(-1)), // bearish
                MakeC("X", 14.4m,16,14.4m,16m,200, now) // bullish engulfing
            };
            var found = det.Detect(candles);
            Assert.Contains(found, f => f.PatternType == Domain.Enums.PatternType.BullishEngulfing);
        }

        [Fact]
        public void Detects_DoubleBottom()
        {
            var opts = new PatternDetectorOptions
            {
                DoubleBottom =
                {
                    MaxLowDiffPercent = 0.05m,
                    MinSeparation = 2,
                    MaxSeparation = 6,
                    MinReboundPercent = 0.05m
                }
            };
            var det = new PatternDetector(opts, NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X",10,11,9.5m,10,100, now.AddMinutes(-5)),
                MakeC("X",10,11.5m,9.6m,11,100, now.AddMinutes(-4)),
                MakeC("X",11,11.2m,9.55m,10.2m,100, now.AddMinutes(-3)),
                MakeC("X",10.5m,11.8m,9.5m,11.5m,100, now.AddMinutes(-2)),
                MakeC("X",11.6m,12,9.52m,10.1m,100, now.AddMinutes(-1)),
                MakeC("X",10.2m,11,9.6m,10.5m,100, now)
            };
            var found = det.Detect(candles);
            Assert.Contains(found, f => f.PatternType == Domain.Enums.PatternType.DoubleBottom);
        }

        [Fact]
        public void Detects_BreakoutAndRetest()
        {
            var opts = new PatternDetectorOptions
            {
                BreakoutAndRetest =
                {
                    Lookback = 5,
                    RetestTolerancePercent = 0.03m
                }
            };
            var det = new PatternDetector(opts, NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X",10,10.5m,9.5m,10,100, now.AddMinutes(-8)),
                MakeC("X",10,10.6m,9.6m,10.1m,100, now.AddMinutes(-7)),
                MakeC("X",10.1m,10.55m,9.7m,10.2m,100, now.AddMinutes(-6)),
                MakeC("X",10.2m,10.58m,9.8m,10.3m,100, now.AddMinutes(-5)),
                MakeC("X",10.3m,10.57m,9.9m,10.4m,100, now.AddMinutes(-4)),
                MakeC("X",10.4m,11.0m,10.2m,11.0m,200, now.AddMinutes(-3)), // breakout
                MakeC("X",11.0m,11.1m,10.9m,11.05m,100, now.AddMinutes(-2)),
                MakeC("X",10.95m,11.0m,10.9m,10.99m,100, now.AddMinutes(-1)), // retest
                MakeC("X",11.05m,11.2m,11.0m,11.15m,150, now)
            };
            var found = det.Detect(candles);
            Assert.Contains(found, f => f.PatternType == Domain.Enums.PatternType.BreakoutAndRetest);
        }

        [Fact]
        public void Detects_VwapReclaim()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X",10,11,9.5m,10.5m,100, now.AddMinutes(-2)),
                MakeC("X",10.5m,11.5m,10,10.2m,100, now.AddMinutes(-1)),
                MakeC("X",10.1m,10.6m,10,10.4m,200, now) // reclaim above VWAP
            };
            var found = det.Detect(candles);
            Assert.Contains(found, f => f.PatternType == Domain.Enums.PatternType.VwapReclaim);
        }

        [Fact]
        public void NoPattern_OnInsufficientData()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var candles = new List<Candle> { MakeC("X",1,2,1,1.5m,100, DateTime.UtcNow) };
            var found = det.Detect(candles);
            Assert.Empty(found);
        }

        [Fact]
        public void HammerShapeWithoutPullback_HasWeakContextScore()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = Enumerable.Range(0, 8)
                .Select(i => MakeC("X", 100 + i, 101 + i, 99 + i, 100.8m + i, 100, now.AddMinutes(-8 + i)))
                .ToList();
            candles.Add(MakeC("X", 108m, 108.2m, 104m, 108.1m, 100, now));

            var hammer = det.Detect(candles).Single(p => p.PatternType == Domain.Enums.PatternType.Hammer);

            Assert.True(decimal.Parse(hammer.Metadata["contextScore"], System.Globalization.CultureInfo.InvariantCulture) < 0.25m);
        }

        [Fact]
        public void BullishEngulfingWithTinyBodies_HasLowQuality()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = Enumerable.Range(0, 18)
                .Select(i => MakeC("X", 100, 103, 97, 100 + (i % 2), 100, now.AddMinutes(-20 + i)))
                .ToList();
            candles.Add(MakeC("X", 100.10m, 103m, 97m, 100.00m, 100, now.AddMinutes(-1)));
            candles.Add(MakeC("X", 99.99m, 103m, 97m, 100.12m, 100, now));

            var engulfing = det.Detect(candles).Single(p => p.PatternType == Domain.Enums.PatternType.BullishEngulfing);

            Assert.True(engulfing.Confidence < 0.6m);
        }

        [Fact]
        public void DoubleBottomWithoutNecklineBreak_IsDetectionButNotConfirmed()
        {
            var opts = new PatternDetectorOptions
            {
                DoubleBottom =
                {
                    MaxLowDiffPercent = 0.05m,
                    MinSeparation = 2,
                    MaxSeparation = 8,
                    MinReboundPercent = 0.03m
                }
            };
            var det = new PatternDetector(opts, NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X", 10, 10.5m, 9.5m, 10, 100, now.AddMinutes(-6)),
                MakeC("X", 10, 11.5m, 10, 11, 100, now.AddMinutes(-5)),
                MakeC("X", 11, 11.2m, 10.2m, 10.5m, 100, now.AddMinutes(-4)),
                MakeC("X", 10.5m, 10.8m, 9.55m, 10.1m, 100, now.AddMinutes(-3)),
                MakeC("X", 10.1m, 10.6m, 9.6m, 10.3m, 100, now.AddMinutes(-2)),
                MakeC("X", 10.3m, 10.8m, 10.1m, 10.7m, 100, now.AddMinutes(-1)),
                MakeC("X", 10.7m, 10.9m, 10.4m, 10.6m, 100, now)
            };

            var pattern = det.Detect(candles).Single(p => p.PatternType == Domain.Enums.PatternType.DoubleBottom);

            Assert.Equal("False", pattern.Metadata["necklineBreak"]);
            Assert.True(decimal.Parse(pattern.Metadata["confirmationScore"], System.Globalization.CultureInfo.InvariantCulture) < 0.5m);
        }

        [Fact]
        public void BreakoutRetestWithoutBounce_DoesNotDetectTradeablePattern()
        {
            var opts = new PatternDetectorOptions
            {
                BreakoutAndRetest =
                {
                    Lookback = 5,
                    RetestTolerancePercent = 0.03m
                }
            };
            var det = new PatternDetector(opts, NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = new List<Candle>
            {
                MakeC("X",10,10.5m,9.5m,10,100, now.AddMinutes(-6)),
                MakeC("X",10,10.6m,9.6m,10.1m,100, now.AddMinutes(-5)),
                MakeC("X",10,11.0m,9.8m,11.0m,200, now.AddMinutes(-4)),
                MakeC("X",11.0m,11.1m,10.9m,11.05m,100, now.AddMinutes(-3)),
                MakeC("X",10.95m,11.0m,10.9m,10.99m,100, now.AddMinutes(-2)),
                MakeC("X",10.98m,11.0m,10.8m,10.95m,100, now)
            };

            var found = det.Detect(candles);

            Assert.DoesNotContain(found, p => p.PatternType == Domain.Enums.PatternType.BreakoutAndRetest);
        }

        [Fact]
        public void VwapReclaimWithTinyDistance_HasNormalizedLowQuality()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = Enumerable.Range(0, 20)
                .Select(i => MakeC("X", 100m, 102m, 98m, 99.8m, 100, now.AddMinutes(-20 + i)))
                .ToList();
            candles.Add(MakeC("X", 99.8m, 102m, 98m, 100.01m, 100, now));

            var reclaim = det.Detect(candles).Single(p => p.PatternType == Domain.Enums.PatternType.VwapReclaim);

            Assert.InRange(reclaim.Confidence, 0m, 0.6m);
            Assert.True(decimal.Parse(reclaim.Metadata["confirmationScore"], System.Globalization.CultureInfo.InvariantCulture) < 0.6m);
        }
    }
}
