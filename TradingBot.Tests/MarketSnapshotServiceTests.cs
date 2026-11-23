using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class MarketSnapshotServiceTests
    {
        [Fact]
        public async Task ExplicitAsOfFiltersBeforeWindowLimitAndExcludesFutureQuotesAndLateCandles()
        {
            using var fixture = new ForexResearchFixture();
            var at = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
            using (var db = fixture.CreateDbContext())
            {
                foreach (var timeframe in new[] { Timeframe.OneMinute, Timeframe.FiveMinutes, Timeframe.FifteenMinutes })
                {
                    for (var i = -30; i <= 30; i++)
                        db.Candles.Add(new TradingBot.Persistence.Candle { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Timeframe = timeframe,
                            TimestampUtc = at.AddMinutes(i), ReceivedTimeUtc = at.AddMinutes(i), IsFinal = true,
                            QualityStatus = MarketDataQualityStatus.Healthy, Open = 100, High = 101, Low = 99, Close = 100, Volume = 100 });
                    db.Candles.Add(new TradingBot.Persistence.Candle { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Timeframe = timeframe,
                        TimestampUtc = at.AddSeconds(-1), ReceivedTimeUtc = at.AddMinutes(2), IsFinal = true,
                        QualityStatus = MarketDataQualityStatus.Healthy, Open = 200, High = 201, Low = 199, Close = 200, Volume = 100 });
                }
                await db.SaveChangesAsync();
            }
            var history = new CandleHistoryService(fixture);
            var window = await history.GetCandlesAsOfAsync("SPY", Timeframe.OneMinute, 3, at);
            Assert.Equal(new[] { at.AddMinutes(-2), at.AddMinutes(-1), at }, window.Select(x => x.TimestampUtc));
            var latest = new LatestMarketDataService();
            latest.Apply(Tick(MarketDataEventKind.Bid, 999m, at.AddMinutes(1)));
            latest.Apply(Tick(MarketDataEventKind.Ask, 1000m, at.AddMinutes(1)));
            var service = new MarketSnapshotService(history, new FeatureEngine(), new FakePatternDetector(), latestMarketData: latest);
            var snapshot = await service.BuildSnapshotAsOfAsync("SPY", at);
            Assert.Equal(at, snapshot.CreatedAtUtc); Assert.Equal(100m, snapshot.CurrentPrice); Assert.Null(snapshot.Spread);
            foreach (var series in new[] { snapshot.OneMinute, snapshot.FiveMinutes, snapshot.FifteenMinutes })
                Assert.All(series.RecentCandles, x => { Assert.True(x.TimestampUtc <= at); Assert.True(x.ReceivedTimeUtc <= at); Assert.Equal(100m, x.Close); });
        }

        [Fact]
        public async Task BuildSnapshotAsync_CombinesOneFiveAndFifteenMinuteContexts()
        {
            var history = new FakeCandleHistoryService();
            history.Seed("SPY", Timeframe.OneMinute, BuildCandles("SPY", Timeframe.OneMinute, 100m, TimeSpan.FromMinutes(1)));
            history.Seed("SPY", Timeframe.FiveMinutes, BuildCandles("SPY", Timeframe.FiveMinutes, 200m, TimeSpan.FromMinutes(5)));
            history.Seed("SPY", Timeframe.FifteenMinutes, BuildCandles("SPY", Timeframe.FifteenMinutes, 300m, TimeSpan.FromMinutes(15)));

            var service = new MarketSnapshotService(
                history,
                new FeatureEngine(emaShort: 3, emaLong: 5, rsi: 3, atr: 3, volAvg: 3),
                new FakePatternDetector());

            var snapshot = await service.BuildSnapshotAsync("SPY", spread: 0.02m);

            Assert.Equal("SPY", snapshot.Symbol);
            Assert.Equal(124.5m, snapshot.CurrentPrice);
            Assert.Equal(0.02m, snapshot.Spread);

            AssertContext(snapshot.OneMinute, Timeframe.OneMinute, "Entry timing", 100m);
            AssertContext(snapshot.FiveMinutes, Timeframe.FiveMinutes, "Trading setup / pullback context", 200m);
            AssertContext(snapshot.FifteenMinutes, Timeframe.FifteenMinutes, "Broader market direction", 300m);

            Assert.Equal(new[] { Timeframe.OneMinute, Timeframe.FiveMinutes, Timeframe.FifteenMinutes }, history.RequestedTimeframes);
        }

        [Fact]
        public async Task BuildSnapshotAsync_UsesProvidedCurrentPriceWhenAvailable()
        {
            var history = new FakeCandleHistoryService();
            history.Seed("SPY", Timeframe.OneMinute, BuildCandles("SPY", Timeframe.OneMinute, 100m, TimeSpan.FromMinutes(1)));
            history.Seed("SPY", Timeframe.FiveMinutes, BuildCandles("SPY", Timeframe.FiveMinutes, 200m, TimeSpan.FromMinutes(5)));
            history.Seed("SPY", Timeframe.FifteenMinutes, BuildCandles("SPY", Timeframe.FifteenMinutes, 300m, TimeSpan.FromMinutes(15)));

            var service = new MarketSnapshotService(
                history,
                new FeatureEngine(emaShort: 3, emaLong: 5, rsi: 3, atr: 3, volAvg: 3),
                new FakePatternDetector());

            var snapshot = await service.BuildSnapshotAsync("SPY", currentPrice: 123.45m);

            Assert.Equal(123.45m, snapshot.CurrentPrice);
        }

        [Fact]
        public async Task BuildSnapshotAsync_UsesLatestCanonicalTradeAndSpread()
        {
            var history = new FakeCandleHistoryService();
            history.Seed("SPY", Timeframe.OneMinute, BuildCandles("SPY", Timeframe.OneMinute, 100m, TimeSpan.FromMinutes(1)));
            history.Seed("SPY", Timeframe.FiveMinutes, BuildCandles("SPY", Timeframe.FiveMinutes, 200m, TimeSpan.FromMinutes(5)));
            history.Seed("SPY", Timeframe.FifteenMinutes, BuildCandles("SPY", Timeframe.FifteenMinutes, 300m, TimeSpan.FromMinutes(15)));
            var latest = new LatestMarketDataService();
            var at = new DateTime(2026, 8, 24, 14, 30, 0, DateTimeKind.Utc);
            latest.Apply(Tick(MarketDataEventKind.Bid, 123.40m, at));
            latest.Apply(Tick(MarketDataEventKind.Ask, 123.44m, at));
            latest.Apply(Tick(MarketDataEventKind.Trade, 123.42m, at));
            var service = new MarketSnapshotService(
                history,
                new FeatureEngine(emaShort: 3, emaLong: 5, rsi: 3, atr: 3, volAvg: 3),
                new FakePatternDetector(),
                latestMarketData: latest);

            var snapshot = await service.BuildSnapshotAsync("SPY");

            Assert.Equal(123.42m, snapshot.CurrentPrice);
            Assert.Equal(0.04m, snapshot.Spread);
            Assert.Equal(at, snapshot.CreatedAtUtc);
            Assert.Equal(0.04m, snapshot.OneMinute.Features!.Spread);
            Assert.Equal(0.04m, snapshot.FiveMinutes.Features!.Spread);
            Assert.Equal(0.04m, snapshot.FifteenMinutes.Features!.Spread);
            Assert.Equal(0L, snapshot.OneMinute.Features.QuoteAgeMilliseconds);
            Assert.StartsWith("features-v3+config-", snapshot.OneMinute.Features.FeatureVersion);
        }

        [Fact]
        public async Task BuildSnapshotAsync_UsesConfiguredCandleHistoryPerTimeframe()
        {
            var history = new FakeCandleHistoryService();
            history.Seed("SPY", Timeframe.OneMinute, BuildCandles("SPY", Timeframe.OneMinute, 100m, TimeSpan.FromMinutes(1), 100));
            history.Seed("SPY", Timeframe.FiveMinutes, BuildCandles("SPY", Timeframe.FiveMinutes, 200m, TimeSpan.FromMinutes(5), 100));
            history.Seed("SPY", Timeframe.FifteenMinutes, BuildCandles("SPY", Timeframe.FifteenMinutes, 300m, TimeSpan.FromMinutes(15), 100));

            var service = new MarketSnapshotService(
                history,
                new FeatureEngine(emaShort: 3, emaLong: 5, rsi: 3, atr: 3, volAvg: 3),
                new FakePatternDetector(),
                Options.Create(new OpenAiSettings
                {
                    MarketContext = new OpenAiMarketContextSettings
                    {
                        OneMinuteCandles = 60,
                        FiveMinuteCandles = 50,
                        FifteenMinuteCandles = 40
                    }
                }));

            var snapshot = await service.BuildSnapshotAsync("SPY");

            Assert.Equal(60, snapshot.OneMinute.RecentCandles.Count);
            Assert.Equal(50, snapshot.FiveMinutes.RecentCandles.Count);
            Assert.Equal(40, snapshot.FifteenMinutes.RecentCandles.Count);
            Assert.Equal(60, history.RequestedCounts[Timeframe.OneMinute]);
            Assert.Equal(50, history.RequestedCounts[Timeframe.FiveMinutes]);
            Assert.Equal(40, history.RequestedCounts[Timeframe.FifteenMinutes]);
        }

        private static void AssertContext(MarketTimeframeSnapshot context, Timeframe timeframe, string interpretation, decimal basePrice)
        {
            Assert.Equal(timeframe, context.Timeframe);
            Assert.Equal(interpretation, context.Interpretation);
            Assert.Equal(25, context.RecentCandles.Count);
            Assert.Equal(basePrice, context.RecentCandles[0].Open);
            Assert.NotNull(context.Features);
            Assert.Equal(timeframe, Assert.Single(context.DetectedPatterns).Timeframe);
            Assert.Contains(context.SupportResistanceCandidates, c => c.Kind == "Support");
            Assert.Contains(context.SupportResistanceCandidates, c => c.Kind == "Resistance");
            Assert.Equal("Up", context.Trend.Label);
            Assert.NotNull(context.Volume.LatestVolume);
            Assert.NotNull(context.Volume.VolumeRatio);
            Assert.NotNull(context.Volatility.Atr);
            Assert.NotNull(context.Volatility.Volatility);
        }

        private static IReadOnlyList<Candle> BuildCandles(string symbol, Timeframe timeframe, decimal basePrice, TimeSpan interval, int count = 25)
        {
            var start = new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);
            return Enumerable.Range(0, count)
                .Select(i =>
                {
                    var open = basePrice + i;
                    var close = open + 0.5m;
                    return new Candle(
                        symbol,
                        timeframe,
                        start.AddTicks(interval.Ticks * i),
                        open,
                        close + 1m,
                        open - 1m,
                        close,
                        1000m + i);
                })
                .ToList();
        }

        private static CanonicalMarketDataEvent Tick(MarketDataEventKind kind, decimal price, DateTime at) => new()
        {
            EventId = $"test-{kind}",
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            Kind = kind,
            EventTimeUtc = at,
            ReceivedTimeUtc = at,
            Source = "Test",
            Price = price,
            Size = 1m
        };

        private sealed class FakeCandleHistoryService : ICandleHistoryService
        {
            private readonly Dictionary<(string Symbol, Timeframe Timeframe), IReadOnlyList<Candle>> _candles = new();
            private readonly List<Timeframe> _requestedTimeframes = new();

            public IReadOnlyList<Timeframe> RequestedTimeframes => _requestedTimeframes;
            public Dictionary<Timeframe, int> RequestedCounts { get; } = new();

            public void Seed(string symbol, Timeframe timeframe, IReadOnlyList<Candle> candles)
            {
                _candles[(symbol, timeframe)] = candles;
            }

            public Task<IReadOnlyList<Candle>> GetLastNCandlesAsync(string symbol, Timeframe timeframe, int n, CancellationToken cancellationToken = default)
            {
                _requestedTimeframes.Add(timeframe);
                RequestedCounts[timeframe] = n;
                var result = _candles.TryGetValue((symbol, timeframe), out var candles)
                    ? candles.TakeLast(n).ToList()
                    : new List<Candle>();

                return Task.FromResult<IReadOnlyList<Candle>>(result);
            }
        }

        private sealed class FakePatternDetector : IPatternDetector
        {
            public IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<Candle> candles)
            {
                var last = candles[^1];
                return new[]
                {
                    new PatternCandidate(
                        PatternType.BreakoutAndRetest,
                        last.Symbol,
                        last.Timeframe,
                        last.TimestampUtc,
                        0.8m,
                        new[] { last.Low, last.High })
                };
            }
        }
    }
}
