using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class PatternDetectorTests
    {
        private static Candle MakeC(
            string sym,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal vol,
            DateTime ts,
            string? instrumentId = null,
            Domain.Enums.Timeframe timeframe = Domain.Enums.Timeframe.OneMinute)
        {
            return new Candle(sym, timeframe, ts, open, high, low, close, vol, instrumentId);
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
                MakeC("X",9.8m,10m,9.5m,9.7m,100, now.AddMinutes(-2)),
                MakeC("X",9.7m,9.9m,9.4m,9.6m,100, now.AddMinutes(-1)),
                MakeC("X",9.8m,10.8m,9.7m,10.7m,200, now) // two closes below VWAP, then reclaim
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
        public void BullishEngulfingWithTinyBodies_FailsExplicitAtrSizeCondition()
        {
            var det = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var now = DateTime.UtcNow;
            var candles = Enumerable.Range(0, 18)
                .Select(i => MakeC("X", 100, 103, 97, 100 + (i % 2), 100, now.AddMinutes(-20 + i)))
                .ToList();
            candles.Add(MakeC("X", 100.10m, 103m, 97m, 100.00m, 100, now.AddMinutes(-1)));
            candles.Add(MakeC("X", 99.99m, 103m, 97m, 100.12m, 100, now));

            var evaluation = det.Evaluate(new PatternDetectionInput { Candles = candles })
                .Single(item => item.PatternType == Domain.Enums.PatternType.BullishEngulfing);

            Assert.False(evaluation.Accepted);
            Assert.Contains(evaluation.HardConditions, condition => condition.Code == "firstBodyAtr" && !condition.Passed);
            Assert.NotEmpty(evaluation.Reasons);
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

        [Theory]
        [InlineData(PatternType.ShootingStar)]
        [InlineData(PatternType.BearishEngulfing)]
        [InlineData(PatternType.DoubleTop)]
        [InlineData(PatternType.BreakdownAndRetest)]
        [InlineData(PatternType.VwapReject)]
        public void DetectsMirroredShortPatternWithExplicitDirection(PatternType expected)
        {
            var options = PatternOptionsForTests();
            var detector = new PatternDetector(options, NullLogger<PatternDetector>.Instance);
            var candles = Mirror(BuildLongPatternSource(expected));

            var candidate = detector.Detect(new PatternDetectionInput
                {
                    Candles = candles,
                    StrategyId = "short-scalp",
                    Directions = new[] { TradeDirection.Short }
                })
                .Single(pattern => pattern.PatternType == expected);

            Assert.Equal(TradeDirection.Short, candidate.Direction);
            Assert.Equal("short-scalp", candidate.StrategyId);
            Assert.All(candidate.HardConditions, condition => Assert.True(condition.Passed, condition.Reason));
            Assert.NotEmpty(candidate.ScoreComponents);
            Assert.StartsWith("patterns-v2+config-", candidate.PatternVersion);
        }

        [Fact]
        public void EveryLongAndShortPatternProvidesExplicitNegativeHardConditionReasons()
        {
            var options = PatternOptionsForTests();
            options.DoubleBottom.MinReboundPercent = 0.5m;
            options.DoubleTop.MinReboundPercent = 0.5m;
            var detector = new PatternDetector(options, NullLogger<PatternDetector>.Instance);
            var now = new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);
            var neutral = Enumerable.Range(0, 30)
                .Select(index => MakeC("SPY", 100m, 101m, 99m, 100m, 1_000m, now.AddMinutes(index), "US-STK-SPY-SMART"))
                .ToArray();

            var evaluations = detector.Evaluate(new PatternDetectionInput
            {
                Candles = neutral,
                StrategyId = "all-patterns",
                Directions = new[] { TradeDirection.Long, TradeDirection.Short }
            });

            Assert.Equal(10, evaluations.Count);
            Assert.All(evaluations, evaluation =>
            {
                Assert.False(evaluation.Accepted);
                Assert.NotEmpty(evaluation.Reasons);
                Assert.Contains(evaluation.HardConditions, condition => !condition.Passed);
            });
        }

        [Fact]
        public void DedupeIdentitySeparatesInstrumentStrategyTimeframeAndDirection()
        {
            var detector = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var at = new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);

            PatternCandidate DetectHammer(string instrumentId, string strategyId, Timeframe timeframe) => detector
                .Detect(new PatternDetectionInput
                {
                    Candles = HammerSeries("SPY", instrumentId, timeframe, at),
                    StrategyId = strategyId,
                    Directions = new[] { TradeDirection.Long }
                })
                .Single(pattern => pattern.PatternType == PatternType.Hammer);

            var basePattern = DetectHammer("US-STK-SPY-SMART", "strategy-a", Timeframe.OneMinute);
            var otherInstrument = DetectHammer("US-STK-SPY-ARCA", "strategy-a", Timeframe.OneMinute);
            var otherStrategy = DetectHammer("US-STK-SPY-SMART", "strategy-b", Timeframe.OneMinute);
            var otherTimeframe = DetectHammer("US-STK-SPY-SMART", "strategy-a", Timeframe.FiveMinutes);
            var shortPattern = detector.Detect(new PatternDetectionInput
                {
                    Candles = Mirror(HammerSeries("SPY", "US-STK-SPY-SMART", Timeframe.OneMinute, at)),
                    StrategyId = "strategy-a",
                    Directions = new[] { TradeDirection.Short }
                })
                .Single(pattern => pattern.PatternType == PatternType.ShootingStar);

            var duplicate = detector.Detect(new PatternDetectionInput
            {
                Candles = HammerSeries("SPY", "US-STK-SPY-SMART", Timeframe.OneMinute, at),
                StrategyId = "strategy-a",
                Directions = new[] { TradeDirection.Long }
            });

            Assert.Equal(5, new[] { basePattern.PatternKey, otherInstrument.PatternKey, otherStrategy.PatternKey, otherTimeframe.PatternKey, shortPattern.PatternKey }.Distinct().Count());
            Assert.DoesNotContain(duplicate, pattern => pattern.PatternType == PatternType.Hammer);
        }

        [Fact]
        public void AcceptedPatternCarriesVersionedIdentityConditionsScoresAndReasons()
        {
            var detector = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var at = new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);

            var candidate = detector.Detect(new PatternDetectionInput
                {
                    Candles = HammerSeries("SPY", "US-STK-SPY-SMART", Timeframe.OneMinute, at),
                    StrategyId = "micro-hammer",
                    FeatureVersion = "features-v2+config-test",
                    Directions = new[] { TradeDirection.Long }
                })
                .Single(pattern => pattern.PatternType == PatternType.Hammer);

            Assert.Equal("US-STK-SPY-SMART", candidate.InstrumentId);
            Assert.Equal("micro-hammer", candidate.StrategyId);
            Assert.Equal("features-v2+config-test", candidate.Context.FeatureVersion);
            Assert.Equal(TradeDirection.Long, candidate.Direction);
            Assert.Contains("ONEMINUTE", candidate.PatternKey);
            Assert.All(candidate.HardConditions, condition => Assert.True(condition.Passed));
            Assert.Equal(candidate.Confidence, candidate.ScoreComponents.Sum(component => component.WeightedScore));
            Assert.NotEmpty(candidate.EvaluationReasons);
        }

        private static PatternDetectorOptions PatternOptionsForTests() => new()
        {
            DoubleBottom = { MaxLowDiffPercent = 0.05m, MinSeparation = 2, MaxSeparation = 6, MinReboundPercent = 0.05m },
            DoubleTop = { MaxLowDiffPercent = 0.05m, MinSeparation = 2, MaxSeparation = 6, MinReboundPercent = 0.05m },
            BreakoutAndRetest = { Lookback = 5, RetestTolerancePercent = 0.03m },
            BreakdownAndRetest = { Lookback = 5, RetestTolerancePercent = 0.03m }
        };

        private static IReadOnlyList<Candle> BuildLongPatternSource(PatternType shortPattern)
        {
            var now = new DateTime(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);
            return shortPattern switch
            {
                PatternType.ShootingStar => HammerSeries("SPY", "US-STK-SPY-SMART", Timeframe.OneMinute, now),
                PatternType.BearishEngulfing => new[]
                {
                    MakeC("SPY", 15m, 16m, 14m, 14.5m, 100m, now.AddMinutes(-1), "US-STK-SPY-SMART"),
                    MakeC("SPY", 14.4m, 16m, 14.4m, 16m, 200m, now, "US-STK-SPY-SMART")
                },
                PatternType.DoubleTop => new[]
                {
                    MakeC("SPY",10m,11m,9.5m,10m,100m,now.AddMinutes(-5),"US-STK-SPY-SMART"),
                    MakeC("SPY",10m,11.5m,9.6m,11m,100m,now.AddMinutes(-4),"US-STK-SPY-SMART"),
                    MakeC("SPY",11m,11.2m,9.55m,10.2m,100m,now.AddMinutes(-3),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.5m,11.8m,9.5m,11.5m,100m,now.AddMinutes(-2),"US-STK-SPY-SMART"),
                    MakeC("SPY",11.6m,12m,9.52m,10.1m,100m,now.AddMinutes(-1),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.2m,11m,9.6m,10.5m,100m,now,"US-STK-SPY-SMART")
                },
                PatternType.BreakdownAndRetest => new[]
                {
                    MakeC("SPY",10m,10.5m,9.5m,10m,100m,now.AddMinutes(-8),"US-STK-SPY-SMART"),
                    MakeC("SPY",10m,10.6m,9.6m,10.1m,100m,now.AddMinutes(-7),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.1m,10.55m,9.7m,10.2m,100m,now.AddMinutes(-6),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.2m,10.58m,9.8m,10.3m,100m,now.AddMinutes(-5),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.3m,10.57m,9.9m,10.4m,100m,now.AddMinutes(-4),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.4m,11m,10.2m,11m,200m,now.AddMinutes(-3),"US-STK-SPY-SMART"),
                    MakeC("SPY",11m,11.1m,10.9m,11.05m,100m,now.AddMinutes(-2),"US-STK-SPY-SMART"),
                    MakeC("SPY",10.95m,11m,10.9m,10.99m,100m,now.AddMinutes(-1),"US-STK-SPY-SMART"),
                    MakeC("SPY",11.05m,11.2m,11m,11.15m,150m,now,"US-STK-SPY-SMART")
                },
                PatternType.VwapReject => new[]
                {
                    MakeC("SPY",9.8m,10m,9.5m,9.7m,100m,now.AddMinutes(-2),"US-STK-SPY-SMART"),
                    MakeC("SPY",9.7m,9.9m,9.4m,9.6m,100m,now.AddMinutes(-1),"US-STK-SPY-SMART"),
                    MakeC("SPY",9.8m,10.8m,9.7m,10.7m,200m,now,"US-STK-SPY-SMART")
                },
                _ => throw new ArgumentOutOfRangeException(nameof(shortPattern))
            };
        }

        private static IReadOnlyList<Candle> HammerSeries(string symbol, string instrumentId, Timeframe timeframe, DateTime at) => new[]
        {
            MakeC(symbol, 10m, 15m, 5m, 12m, 100m, at.AddMinutes(-1), instrumentId, timeframe),
            MakeC(symbol, 12m, 12.5m, 9m, 12.2m, 50m, at, instrumentId, timeframe)
        };

        private static IReadOnlyList<Candle> Mirror(IReadOnlyList<Candle> source, decimal axis = 30m) => source
            .Select(candle => new Candle(
                candle.Symbol,
                candle.Timeframe,
                candle.TimestampUtc,
                axis - candle.Open,
                axis - candle.Low,
                axis - candle.High,
                axis - candle.Close,
                candle.Volume,
                candle.InstrumentId,
                candle.ReceivedTimeUtc,
                candle.Source,
                candle.IsFinal,
                candle.QualityStatus))
            .ToArray();
    }
}
