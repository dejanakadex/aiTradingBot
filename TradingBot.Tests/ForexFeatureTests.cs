using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using Candle = TradingBot.Domain.Models.Candle;

namespace TradingBot.Tests;

public sealed class ForexFeatureTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TradingSettings Trading = ForexCollectionTests.Settings();
    private static ShortAggregateSettings Settings() => new() { IntervalsSeconds = [1, 5, 15], ForexMaximumQuoteAgeSeconds = 5 };
    private static LatestMarketDataService Latest(ShortAggregateSettings? settings = null) => new(Options.Create(settings ?? Settings()), Options.Create(Trading));
    private static ForexFeatureService Features(ILatestMarketDataService latest, ShortAggregateSettings? settings = null) =>
        new(latest, new FeatureEngine(), Options.Create(Trading), Options.Create(settings ?? Settings()));

    [Theory]
    [InlineData(1)] [InlineData(5)] [InlineData(15)]
    public void PairedQuotesProduceFinalMidpointBarsWithoutTradeVolume(int interval)
    {
        var latest = Latest();
        var one = Pair("one", 0.1, 0.2, 1.1m, 1.1002m);
        latest.Apply(one[0]);
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", interval, Start.AddHours(1)));
        latest.Apply(one[1]);
        foreach (var item in Pair("two", 0.5, 0.6, 1.1004m, 1.1008m)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(interval + 2));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", interval, Start.AddSeconds(interval + 1.99)));
        var bar = Assert.Single(latest.GetCompletedForexAggregates("FX-EURUSD", interval, Start.AddSeconds(interval + 2)));
        Assert.Equal(1.1001m, bar.Open); Assert.Equal(1.1006m, bar.High); Assert.Equal(1.1001m, bar.Low); Assert.Equal(1.1006m, bar.Close);
        Assert.Equal(0.0003m, bar.MeanSpread); Assert.Equal(0.0004m, bar.MaximumSpread);
        Assert.Equal(2, bar.QuoteCount); Assert.Null(bar.TradeVolume);
        Assert.Empty(latest.GetCompletedShortAggregates("FX-EURUSD", interval, Start.AddHours(1)));
        Assert.Null(latest.Get("FX-EURUSD")!.LastTrade);
    }

    [Fact]
    public void PairIdentityPreventsMixingCallbacksAndDeduplicatesWholeQuotes()
    {
        var latest = Latest();
        var first = Pair("first", 1, 1.1, 1m, 1.2m);
        var second = Pair("second", 2, 2.1, 3m, 3.2m);
        latest.Apply(first[0]); latest.Apply(second[1]);
        latest.AdvanceTo(Start.AddSeconds(3));
        latest.Apply(second[0]); latest.Apply(first[1]);
        foreach (var item in first.Concat(second)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(7));
        var bar = Assert.Single(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
        Assert.Equal(2, bar.QuoteCount); Assert.Equal(1.1m, bar.Open); Assert.Equal(3.1m, bar.Close);
        Assert.Equal(3.1m, bar.High); // No mixed 1/3.2 callback midpoint.
    }

    [Theory]
    [InlineData("crossed")] [InlineData("stale")] [InlineData("future")]
    [InlineData("legacy")] [InlineData("mismatched-time")] [InlineData("nonfinal")]
    public void InvalidQuotesNeverEnterMidpointBars(string kind)
    {
        var latest = Latest();
        var pair = Pair("bad", 1, 1.1, 1m, 1.2m);
        pair = kind switch
        {
            "crossed" => Pair("bad", 1, 1.1, 2m, 1m),
            "stale" => Pair("bad", 1, 20, 1m, 1.2m),
            "future" => Pair("bad", 2, 1, 1m, 1.2m),
            "legacy" => pair.Select(x => x with { Source = "IBKR.TickByTick.Forex.BidAsk" }).ToArray(),
            "mismatched-time" => [pair[0], pair[1] with { ReceivedTimeUtc = Start.AddSeconds(1.2) }],
            "nonfinal" => pair.Select(x => x with { IsFinal = false }).ToArray(), _ => pair
        };
        foreach (var item in pair) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(30));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(30)));
        Assert.True(latest.GetForexDiagnostics("FX-EURUSD").RejectedQuoteEvents > 0);
    }

    [Fact]
    public void IncompleteLateAndMissingIntervalsAreExplicitAndCannotRewriteFinalBars()
    {
        var latest = Latest();
        foreach (var item in Pair("ok", 1, 1.1, 1m, 1.2m)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(7));
        var original = Assert.Single(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
        latest.Apply(Pair("half", 8, 8.1, 1m, 1.2m)[0]);
        foreach (var item in Pair("late", 4.9, 7.1, 99m, 100m)) latest.Apply(item);
        foreach (var item in Pair("next", 21, 21.1, 2m, 2.2m)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(27));
        var bars = latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(27));
        Assert.Equal(2, bars.Count); Assert.Equal(original, bars[0]); Assert.Equal(3, bars[1].EmptyIntervalsBefore);
        Assert.Equal(1, latest.GetForexDiagnostics("FX-EURUSD").IncompleteQuotes);
        Assert.True(latest.GetForexDiagnostics("FX-EURUSD").LateQuotes > 0);
        var f = Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(27))!;
        Assert.Equal(1, f.SampleCount); Assert.False(f.PriceFeaturesReady); Assert.Null(f.EmaLong);
    }

    [Fact]
    public void PriceFeaturesWarmUpButVolumeAndVwapRemainExplicitlyUnavailable()
    {
        var latest = Latest();
        foreach (var item in Series(40)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(202));
        var f = Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(202))!;
        Assert.Equal(40, f.SampleCount); Assert.True(f.PriceFeaturesReady);
        Assert.Equal("QuoteMidpoint", f.PriceBasis); Assert.StartsWith("features-fx-midpoint-v1+", f.FeatureVersion);
        Assert.NotNull(f.EmaShort); Assert.NotNull(f.EmaLong); Assert.NotNull(f.Rsi); Assert.NotNull(f.Atr);
        Assert.NotNull(f.Spread); Assert.Equal(3000, f.QuoteAgeMilliseconds);
        Assert.Null(f.Vwap); Assert.Null(f.VolumeAverage); Assert.Null(f.VolumeRatio); Assert.Null(f.DollarVolume);
        Assert.Null(f.NormalizedLiquidity); Assert.Null(f.DistanceFromVwapPercent);
        Assert.Contains("Trade volume is unavailable", f.UnavailableFeatures!["Vwap"]);
        Assert.Null(Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(6)));
        var stale = Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(250))!;
        Assert.Null(stale.Spread); Assert.Contains("Spread", stale.UnavailableFeatures!.Keys);
    }

    [Fact]
    public void FeatureAsOfExcludesFutureReceivedBarsAndRequiresExplicitMidpointContract()
    {
        var engine = new FeatureEngine();
        Candle Bar(int offset, int received, decimal price, string source = "IBKR.Midpoint", decimal? volume = null) =>
            new("EURUSD", Timeframe.FiveSeconds, Start.AddSeconds(offset), price, price, price, price, volume,
                "FX-EURUSD", Start.AddSeconds(received), source);
        var bars = new[] { Bar(5, 7, 1m), Bar(10, 12, 1.1m), Bar(10, 100, 99m) };
        var f = engine.ComputeFeatures(new CanonicalFeatureInput { ForexMidpoint = true, Candles = bars, AsOfUtc = Start.AddSeconds(12) });
        Assert.Equal(2, f.SampleCount); Assert.Equal(10m, f.PriceChangePercent);
        Assert.Throws<NotSupportedException>(() => engine.ComputeFeatures(bars));
        Assert.Throws<ArgumentException>(() => engine.ComputeFeatures(new CanonicalFeatureInput
        { ForexMidpoint = true, Candles = [Bar(5, 7, 1m, volume: 0m)], AsOfUtc = Start.AddSeconds(12) }));
        Assert.Throws<ArgumentException>(() => engine.ComputeFeatures(new CanonicalFeatureInput
        { ForexMidpoint = true, Candles = [bars[0], Bar(10, 12, 1m, "FX.QuoteMidpoint.other")], AsOfUtc = Start.AddSeconds(12) }));
    }

    [Fact]
    public void StockAndTwoForexPairsRemainIndependentWithBoundedRetention()
    {
        var trading = ForexCollectionTests.Settings();
        trading.Instruments = [.. trading.Instruments, new() { InstrumentId = "FX-EURGBP", SecurityType = "CASH", BaseCurrency = "EUR", Currency = "GBP",
            Exchange = "IDEALPRO", Enabled = true, TradingEnabled = false, StrategyIds = ["fx-research"] }, new() { InstrumentId = "SPY", Symbol = "SPY" }];
        var settings = Settings(); settings.MaxCompletedPerInterval = 2;
        var latest = new LatestMarketDataService(Options.Create(settings), Options.Create(trading));
        foreach (var item in Series(5))
        {
            latest.Apply(item);
            latest.Apply(item with { InstrumentId = "FX-EURGBP", Symbol = "EURGBP", Price = item.Price / 2m });
        }
        latest.Apply(new CanonicalMarketDataEvent { InstrumentId = "SPY", Symbol = "SPY", Kind = MarketDataEventKind.Trade,
            EventId = "stock", Price = 100m, Size = 3m, EventTimeUtc = Start.AddSeconds(24), ReceivedTimeUtc = Start.AddSeconds(24.1) });
        latest.AdvanceTo(Start.AddSeconds(27));
        var eur = latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(27));
        var gbp = latest.GetCompletedForexAggregates("FX-EURGBP", 5, Start.AddSeconds(27));
        Assert.Equal(2, eur.Count); Assert.Equal(eur[^1].Close / 2m, gbp[^1].Close);
        var stock = Assert.Single(latest.GetCompletedShortAggregates("SPY", 5, Start.AddSeconds(27)));
        Assert.Equal(3m, stock.Volume); Assert.Empty(latest.GetCompletedForexAggregates("SPY", 5, Start.AddSeconds(27)));
    }

    [Fact]
    public async Task ActualAdapterPreservesAtomicPairIdentityAndReceiptAcrossParquetContract()
    {
        await using var sub = new IbkrBrokerService.TickSubscription("FX-EURUSD", "EURUSD", 16, () => { }, ForexQuoteAggregator.QuoteSource, false);
        sub.PublishQuote(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1.1, 1.1002, 100, 200);
        Assert.True(sub.Reader.TryRead(out var bid)); Assert.True(sub.Reader.TryRead(out var ask));
        Assert.Equal(bid.ReceivedTimeUtc, ask.ReceivedTimeUtc);
        Assert.Equal(bid.EventId[..^4], ask.EventId[..^4]);
        Assert.EndsWith(":bid", bid.EventId); Assert.EndsWith(":ask", ask.EventId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void KnownInvalidOrIncompleteQuoteSuppressesItsWholeWindow(bool incomplete)
    {
        var latest = Latest();
        foreach (var item in Pair("valid", 1, 1.1, 1m, 1.2m)) latest.Apply(item);
        var bad = Pair("bad", 2, 2.1, 2m, 1m);
        latest.Apply(bad[0]); if (!incomplete) latest.Apply(bad[1]);
        latest.AdvanceTo(Start.AddSeconds(7));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
        foreach (var item in Pair("after", 6, 7.1, 1m, 1.2m)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(17));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", 15, Start.AddSeconds(17)));
    }

    [Fact]
    public void ReconnectSessionAndExplicitResetRestartWarmup()
    {
        var latest = Latest();
        foreach (var item in Series(30)) latest.Apply(item);
        latest.AdvanceTo(Start.AddSeconds(152));
        Assert.True(Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(152))!.PriceFeaturesReady);
        foreach (var item in Pair("reconnected", 154, 154.1, 1m, 1.2m))
            latest.Apply(item with { EventId = item.EventId.Replace(":test:", ":new-session:") });
        latest.AdvanceTo(Start.AddSeconds(157));
        var frame = Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(157))!;
        Assert.Equal(1, frame.SampleCount); Assert.False(frame.PriceFeaturesReady);
        latest.ResetForexAggregates("FX-EURUSD");
        Assert.Null(Features(latest).Compute("FX-EURUSD", 5, Start.AddSeconds(157)));
    }

    [Fact]
    public void QuoteContractConfigurationChangesFxVersionAndTiedEventsAreDeterministic()
    {
        var config = Settings(); var version = ForexQuoteAggregator.VersionFor(config);
        config.ForexMaximumQuoteAgeSeconds++;
        Assert.NotEqual(version, ForexQuoteAggregator.VersionFor(config));
        var left = Latest(); var right = Latest();
        var ticks = Pair("2", 1, 1.1, 1m, 1.2m).Concat(Pair("10", 1, 1.1, 2m, 2.2m)).ToArray();
        foreach (var tick in ticks) left.Apply(tick);
        foreach (var tick in ticks.Reverse()) right.Apply(tick);
        left.AdvanceTo(Start.AddSeconds(7)); right.AdvanceTo(Start.AddSeconds(7));
        Assert.Equal(left.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)),
            right.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
    }

    [Fact]
    public async Task PersistedQuotesReplaySameFeatureFramesAsLiveWithoutPatternOrDatabaseCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), "fx5-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = root }), new Clock(), NullLogger<ParquetMarketDatasetStore>.Instance);
            var ticks = Series(40).ToArray();
            var live = Latest(); var service = Features(live); var frames = new List<MarketFeatures>();
            for (var i = 0; i < 40; i++)
            {
                live.Apply(ticks[i * 2]); live.Apply(ticks[i * 2 + 1]);
                var now = Start.AddSeconds((i + 1) * 5 + 2);
                live.AdvanceTo(now);
                frames.Add(service.Compute("FX-EURUSD", 5, now)!);
            }
            var quality = new MarketDataQualityAssessment(MarketDataQualityStatus.Healthy, "fixture", true, false);
            await store.WriteBatchAsync(ticks.Reverse().Select(x => MarketDatasetRecord.From(x, quality, PipelineContractVersions.MarketData)).ToArray());
            Assert.True((await store.VerifyAsync()).IsValid);
            var replay = new DeterministicReplayService(store, new NoDatabase(), new FeatureEngine(), new NoPatterns(), new Clock(),
                Options.Create(new ReplaySettings()), NullLogger<DeterministicReplayService>.Instance,
                tradingSettings: Options.Create(Trading), shortAggregateSettings: Options.Create(Settings()));
            var first = await replay.ReplayForexFeaturesAsync("FX-EURUSD", Start, Start.AddSeconds(202), 5);
            var second = await replay.ReplayForexFeaturesAsync("FX-EURUSD", Start, Start.AddSeconds(202), 5);
            Assert.Equal(40, first.Frames.Count); Assert.Equal(first.InputSha256, second.InputSha256);
            for (var i = 0; i < frames.Count; i++)
                foreach (var property in typeof(MarketFeatures).GetProperties())
                {
                    if (property.Name == nameof(MarketFeatures.UnavailableFeatures))
                        Assert.Equal(frames[i].UnavailableFeatures!.OrderBy(x => x.Key), first.Frames[i].UnavailableFeatures!.OrderBy(x => x.Key));
                    else Assert.Equal(property.GetValue(frames[i]), property.GetValue(first.Frames[i]));
                }
            Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            var clipped = await replay.ReplayForexFeaturesAsync("FX-EURUSD", Start, Start.AddSeconds(201), 5);
            Assert.Equal(39, clipped.Frames.Count);
            await Assert.ThrowsAsync<NotSupportedException>(() => replay.StartAsync(new ReplayStartRequest("FX-EURUSD", Start, Start.AddSeconds(202), StrategyId: "fx-research")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static IEnumerable<CanonicalMarketDataEvent> Series(int count) => Enumerable.Range(0, count)
        .SelectMany(i => Pair(i.ToString(), i * 5 + 4, i * 5 + 4.1, 1.1m + i * 0.00001m, 1.1002m + i * 0.00001m));
    private static CanonicalMarketDataEvent[] Pair(string id, double at, double received, decimal bid, decimal ask) =>
        new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask }.Select(kind => new CanonicalMarketDataEvent
        {
            EventId = $"IBKR:FXQ:test:{id}:{kind.ToString().ToLowerInvariant()}", InstrumentId = "FX-EURUSD", Symbol = "EURUSD",
            Kind = kind, EventTimeUtc = Start.AddSeconds(at), ReceivedTimeUtc = Start.AddSeconds(received),
            Source = ForexQuoteAggregator.QuoteSource, Price = kind == MarketDataEventKind.Bid ? bid : ask, Size = 100000m
        }).ToArray();
    private sealed class Clock : IClock { public DateTime UtcNow => Start; }
    private sealed class NoDatabase : IDbContextFactory<TradingBotDbContext> { public TradingBotDbContext CreateDbContext() => throw new InvalidOperationException("FX feature replay must not touch operational state."); }
    private sealed class NoPatterns : IPatternDetectorFactory
    {
        public string DetectorVersion => "unused";
        public IPatternDetector Create() => throw new InvalidOperationException("FX-5 must not execute stock patterns.");
    }
}
