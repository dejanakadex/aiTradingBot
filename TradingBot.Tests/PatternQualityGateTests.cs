using Microsoft.Extensions.Options;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class PatternQualityGateTests
    {
        private static readonly DateTime Now = new(2026, 8, 25, 14, 30, 0, DateTimeKind.Utc);

        [Fact]
        public void RejectsPatternFoundOnFiveMinuteTimeframeByDefault()
        {
            var gate = CreateGate();
            var pattern = Pattern(PatternType.Hammer, Timeframe.FiveMinutes, 0.9m);

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out _, out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("timeframe"));
        }

        [Fact]
        public void AllowsFiveMinuteTimeframeWhenExplicitlyConfigured()
        {
            var gate = CreateGate(new PatternDetectorOptions
            {
                AiAnalysisTimeframes = new[] { "1m", "5m" },
                MinimumOneMinuteCandlesForTradeSetup = 1,
                MinimumFiveMinuteCandlesForTradeSetup = 1,
                MinimumFifteenMinuteCandlesForTradeSetup = 1
            });
            var pattern = Pattern(PatternType.Hammer, Timeframe.FiveMinutes, 0.9m);

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out _, out var reasons);

            Assert.True(approved, string.Join("; ", reasons));
        }

        [Fact]
        public void RejectsBearishFifteenMinuteContextWhenConfiguredForLongSetup()
        {
            var gate = CreateGate();

            var approved = gate.TryCreateTradeSetup(
                Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m),
                Snapshot(fifteenTrend: -1),
                out _,
                out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("15m trend"));
        }

        [Fact]
        public void AllowsBearishFifteenMinuteContextWhenStrategyConfigDisablesThatRule()
        {
            var gate = CreateGate(new PatternDetectorOptions
            {
                RejectLongWhenFifteenMinuteTrendBearish = false,
                MinimumOneMinuteCandlesForTradeSetup = 1,
                MinimumFiveMinuteCandlesForTradeSetup = 1,
                MinimumFifteenMinuteCandlesForTradeSetup = 1
            });

            var approved = gate.TryCreateTradeSetup(
                Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m),
                Snapshot(fifteenTrend: -1),
                out _,
                out var reasons);

            Assert.True(approved, string.Join("; ", reasons));
        }

        [Fact]
        public void RejectsExtremeVolatility()
        {
            var gate = CreateGate();

            var approved = gate.TryCreateTradeSetup(
                Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m),
                Snapshot(oneMinuteVolatility: 0.2m),
                out _,
                out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("volatility"));
        }

        [Fact]
        public void RejectsInsufficientHigherTimeframeContext()
        {
            var gate = CreateGate();

            var approved = gate.TryCreateTradeSetup(
                Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m),
                Snapshot(fiveMinuteCandles: 2, fifteenMinuteCandles: 2),
                out _,
                out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("5m context"));
            Assert.Contains(reasons, r => r.Contains("15m context"));
        }

        [Fact]
        public void RejectsPatternNotOnLatestOneMinuteCandle()
        {
            var gate = CreateGate();

            var approved = gate.TryCreateTradeSetup(
                Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m, Now.AddMinutes(-2)),
                Snapshot(),
                out _,
                out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("latest completed 1m candle"));
        }

        [Fact]
        public void RejectsDoubleBottomWithoutNecklineConfirmation()
        {
            var gate = CreateGate();
            var pattern = Pattern(PatternType.DoubleBottom, Timeframe.OneMinute, 0.95m, metadata: new Dictionary<string, string>
            {
                ["confirmationScore"] = "0.2",
                ["necklineBreak"] = "false",
                ["finalQuality"] = "0.95"
            });

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out _, out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("neckline"));
        }

        [Fact]
        public void RejectsBreakoutAndRetestWithoutRequiredVolumeConfirmation()
        {
            var gate = CreateGate();
            var pattern = Pattern(PatternType.BreakoutAndRetest, Timeframe.OneMinute, 0.85m, metadata: new Dictionary<string, string>
            {
                ["volumeScore"] = "0.1",
                ["confirmationScore"] = "0.8",
                ["finalQuality"] = "0.85"
            });

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out _, out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, r => r.Contains("volume score"));
        }

        [Fact]
        public void ApprovedSetupContainsQualityBreakdown()
        {
            var gate = CreateGate();
            var pattern = Pattern(PatternType.Hammer, Timeframe.OneMinute, 0.9m);

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out var setup, out var reasons);

            Assert.True(approved, string.Join("; ", reasons));
            Assert.Equal(pattern, setup.Pattern);
            Assert.Equal(0.9m, setup.Quality.FinalQuality);
            Assert.Equal(0.9m, setup.Quality.GeometryScore);
        }

        [Fact]
        public void ShortPatternIsExplicitlyResearchOnlyUntilDownstreamExecutionSupportsIt()
        {
            var gate = CreateGate();
            var pattern = new PatternCandidate(
                PatternType.ShootingStar,
                "SPY",
                Timeframe.OneMinute,
                Now,
                0.9m,
                new[] { 99m, 101m },
                new Dictionary<string, string>
                {
                    ["geometryScore"] = "0.9",
                    ["contextScore"] = "0.8",
                    ["confirmationScore"] = "0.8",
                    ["volumeScore"] = "0.8",
                    ["locationScore"] = "0.8",
                    ["finalQuality"] = "0.9"
                },
                direction: TradeDirection.Short);

            var approved = gate.TryCreateTradeSetup(pattern, Snapshot(), out _, out var reasons);

            Assert.False(approved);
            Assert.Contains(reasons, reason => reason.Contains("research-only", StringComparison.OrdinalIgnoreCase));
        }

        private static PatternQualityGate CreateGate(PatternDetectorOptions? options = null)
        {
            options ??= new PatternDetectorOptions
            {
                MinimumOneMinuteCandlesForTradeSetup = 20,
                MinimumFiveMinuteCandlesForTradeSetup = 10,
                MinimumFifteenMinuteCandlesForTradeSetup = 10
            };

            return new PatternQualityGate(Options.Create(options));
        }

        private static PatternCandidate Pattern(
            PatternType type,
            Timeframe timeframe,
            decimal quality,
            DateTime? detectedAt = null,
            IDictionary<string, string>? metadata = null)
        {
            var scores = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["geometryScore"] = "0.9",
                ["contextScore"] = "0.8",
                ["confirmationScore"] = "0.8",
                ["volumeScore"] = "0.8",
                ["locationScore"] = "0.8",
                ["finalQuality"] = quality.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
            };

            if (metadata != null)
            {
                foreach (var pair in metadata)
                {
                    scores[pair.Key] = pair.Value;
                }
            }

            return new PatternCandidate(type, "SPY", timeframe, detectedAt ?? Now, quality, new[] { 99m, 101m }, scores);
        }

        private static MarketSnapshot Snapshot(
            int oneMinuteCandles = 25,
            int fiveMinuteCandles = 15,
            int fifteenMinuteCandles = 15,
            int fiveTrend = 1,
            int fifteenTrend = 1,
            decimal oneMinuteVolatility = 0.01m)
        {
            return new MarketSnapshot
            {
                Symbol = "SPY",
                CreatedAtUtc = Now,
                CurrentPrice = 100m,
                OneMinute = Context(Timeframe.OneMinute, oneMinuteCandles, 1, oneMinuteVolatility),
                FiveMinutes = Context(Timeframe.FiveMinutes, fiveMinuteCandles, fiveTrend, 0.01m),
                FifteenMinutes = Context(Timeframe.FifteenMinutes, fifteenMinuteCandles, fifteenTrend, 0.01m)
            };
        }

        private static MarketTimeframeSnapshot Context(Timeframe timeframe, int count, int trend, decimal volatility)
        {
            var minutes = timeframe == Timeframe.OneMinute ? 1 : timeframe == Timeframe.FiveMinutes ? 5 : 15;
            var candles = Enumerable.Range(0, count)
                .Select(i => new Candle("SPY", timeframe, Now.AddMinutes((i - count + 1) * minutes), 100m, 101m, 99m, 100m + i * 0.01m, 1000m))
                .ToArray();

            return new MarketTimeframeSnapshot
            {
                Timeframe = timeframe,
                RecentCandles = candles,
                Trend = new TrendInformation { Direction = trend, Label = trend > 0 ? "Up" : trend < 0 ? "Down" : "Flat" },
                Volatility = new VolatilityContext { Volatility = volatility }
            };
        }
    }
}
