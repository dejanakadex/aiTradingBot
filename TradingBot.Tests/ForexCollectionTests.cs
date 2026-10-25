using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class ForexCollectionTests
{
    internal static TradingSettings Settings() => new()
    {
        Instruments = [new() { InstrumentId = "FX-EURUSD", BaseCurrency = "EUR", Currency = "USD",
            SecurityType = "CASH", Exchange = "IDEALPRO", Enabled = true, TradingEnabled = false,
            StrategyIds = ["fx-research"], MarketDataTimeframes = ["1m"] }]
    };

    [Fact]
    public void CollectionIsAllowedWithTradingAndLiveDisabled()
    {
        var settings = Settings();
        Assert.Empty(settings.GetValidationErrors());
        Assert.False(settings.Enabled);
        Assert.False(settings.LiveTradingExplicitlyEnabled);
        Assert.Equal(TradingOperatingMode.AnalysisOnly, settings.OperatingMode);
        var instrument = Assert.Single(settings.GetEnabledInstruments());
        Assert.False(instrument.TradingEnabled);
        var fx = IbkrDataProfile.For(instrument);
        Assert.Equal("MIDPOINT", fx.WhatToShow);
        Assert.Equal(0, fx.UseRegularHours);
        Assert.False(fx.RequestTrades);
        var stock = IbkrDataProfile.For(new TradingSettings().GetConfiguredInstruments().Single());
        Assert.Equal("TRADES", stock.WhatToShow);
        Assert.Equal(1, stock.UseRegularHours);
        Assert.True(stock.RequestTrades);
    }

    [Theory]
    [InlineData("2026-10-23T20:59:00Z", true)]
    [InlineData("2026-10-23T21:00:00Z", false)]
    [InlineData("2026-10-24T12:00:00Z", false)]
    [InlineData("2026-10-25T21:14:00Z", false)]
    [InlineData("2026-10-25T21:15:00Z", true)]
    [InlineData("2026-10-26T21:00:00Z", false)]
    [InlineData("2026-10-26T21:15:00Z", true)]
    [InlineData("2026-11-01T21:15:00Z", false)]
    [InlineData("2026-11-01T22:15:00Z", true)]
    [InlineData("2026-10-26T02:00:00Z", true)]
    public void ForexSessionIncludesOvernightAndHonorsWeekendBreakAndUsDst(string instant, bool expected) =>
        Assert.Equal(expected, MarketDataSession.IsForexOpen(DateTime.Parse(instant).ToUniversalTime()));

    [Fact]
    public void InterruptedTickCoverageExcludesKnownWeekendAndMaintenance()
    {
        var start = new DateTime(2026, 10, 23, 20, 59, 30, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 25, 21, 15, 30, DateTimeKind.Utc);
        var windows = MarketDataSession.ForexOpenWindows(start, end).ToArray();
        Assert.Equal(2, windows.Length);
        Assert.Equal(TimeSpan.FromSeconds(30), windows[0].End - windows[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(30), windows[1].End - windows[1].Start);
        Assert.Empty(MarketDataSession.ForexOpenWindows(windows[0].End, windows[1].Start));
    }

    [Fact]
    public async Task ActualAdapterQuotesRemainQuotesAndRefreshBothSides()
    {
        await using var subscription = new IbkrBrokerService.TickSubscription("FX-EURUSD", "EURUSD", 128, () => { },
            "IBKR.TickByTick.Forex.BidAsk", false);
        var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        subscription.PublishQuote(at, 1.12345, 1.12350, 100000, 200000);
        subscription.PublishQuote(at + 1, 1.12345, 1.12355, 100000, 200000);
        subscription.Publish(MarketDataEventKind.Trade, at, 1.12345, 100);
        var events = new List<CanonicalMarketDataEvent>();
        while (subscription.Reader.TryRead(out var item)) events.Add(item);
        Assert.Equal(4, events.Count);
        Assert.All(events, item => { Assert.Null(item.Volume); Assert.NotEqual(MarketDataEventKind.Trade, item.Kind); });
        Assert.Equal(events[2].EventTimeUtc, events[3].EventTimeUtc);
        Assert.Equal(100000m, events[2].Size);
        Assert.Equal("EURUSD", events[0].Symbol);
        subscription.PublishQuote(at + 2, 1.12345, 1.12355, -1m, decimal.MaxValue);
        Assert.True(subscription.Reader.TryRead(out var unknownBid));
        Assert.True(subscription.Reader.TryRead(out var unknownAsk));
        Assert.Null(unknownBid.Size);
        Assert.Null(unknownAsk.Size);
    }

    [Fact]
    public void AdapterMapsUnavailableBrokerVolumeToNullOnlyForMidpoint()
    {
        var raw = new IBApi.Bar("1792789200", 1.1, 1.2, 1.0, 1.15, -1m, 0, -1m);
        var fx = IbkrBrokerService.ToMarketBar("EURUSD", "1m", raw, IbkrDataProfile.For(Settings().GetConfiguredInstruments()[0]));
        Assert.Null(fx.Volume);
        Assert.Equal("IBKR.Midpoint", fx.Source);
        Assert.Equal(1.15m, fx.Close);
        var stock = IbkrBrokerService.ToMarketBar("SPY", "1m", raw);
        Assert.Equal(-1m, stock.Volume); // Still invalid; only a known quote profile lacks volume.
    }

    [Fact]
    public async Task FxPipelinePersistsNullVolumeAndNeverPublishesStockAnalysis()
    {
        using var fixture = new Fixture();
        var settings = Settings();
        var sink = new Sink();
        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        using var pipeline = new MarketDataPipeline(new FakeIbkrAdapter(), bus, fixture,
            new MarketDataValidator(Options.Create(settings), fixture.Clock), null, NullLogger<MarketDataPipeline>.Instance,
            settings: Options.Create(settings), clock: fixture.Clock, datasetSink: sink);
        await pipeline.ProcessMarketBarAsync(Bar(fixture.Clock.UtcNow.AddMinutes(-1)), source: "IBKR.StreamingBar");
        using var db = fixture.CreateDbContext();
        var row = Assert.Single(db.Candles);
        Assert.Null(row.Volume);
        Assert.Equal("IBKR.Midpoint", row.Source);
        var record = Assert.Single(sink.Records);
        Assert.Null(record.Volume);
        Assert.False(record.CanTriggerTrading);
        Assert.False(bus.CandleReader.TryRead(out _));
        var candles = await new CandleHistoryService(fixture).GetLastNCandlesAsync("EURUSD", Timeframe.OneMinute, 10);
        Assert.False(candles.Single().HasTradeVolume);
        Assert.Throws<NotSupportedException>(() => new FeatureEngine().ComputeFeatures(candles));
    }

    [Fact]
    public async Task GapQualityDistinguishesDailyBreakFromMissingMidnightBars()
    {
        using var fixture = new Fixture();
        var quality = new MarketDataQualityService(fixture, Options.Create(Settings()), fixture.Clock,
            NullLogger<MarketDataQualityService>.Instance);
        var before = new DateTime(2026, 10, 26, 20, 59, 0, DateTimeKind.Utc);
        Assert.True((await quality.EvaluateAsync(Canonical(before), true)).IsHealthy);
        Assert.True((await quality.EvaluateAsync(Canonical(before.AddMinutes(16)), true)).IsHealthy);
        Assert.Equal(MarketDataQualityStatus.GapDetected,
            (await quality.EvaluateAsync(Canonical(new DateTime(2026, 10, 27, 0, 1, 0, DateTimeKind.Utc)), true)).Status);
        var fabricated = Canonical(before.AddDays(1)) with { Volume = 0m };
        Assert.False((await quality.EvaluateAsync(fabricated, true)).CanPersist);
    }

    [Fact]
    public async Task BackfillRestartGapReportAndParquetPreserveFxProvenance()
    {
        using var fixture = new Fixture();
        var settings = Settings();
        var registry = new InstrumentRegistryService(fixture, Options.Create(settings), fixture.Clock);
        await registry.SynchronizeConfiguredAsync();
        var data = new History();
        var sink = new Sink();
        var options = Options.Create(new HistoricalBackfillSettings
        {
            PacingDelayMilliseconds = 0, OneMinute = new() { LookbackDays = 1, SegmentDays = 1 }
        });
        HistoricalBackfillService Service() => new(fixture, data, registry, options, fixture.Clock,
            NullLogger<HistoricalBackfillService>.Instance, sink);
        var service = Service();
        await service.SynchronizePlanAsync();
        var result = await service.RunNextSegmentAsync();
        Assert.Equal(HistoricalBackfillStatus.CompletedWithGaps, result!.Status);
        var gaps = await service.GetGapsAsync();
        Assert.NotEmpty(gaps);
        Assert.All(gaps, gap => Assert.True(MarketDataSession.IsForexOpen(gap.StartUtc)));
        Assert.Contains(gaps, gap => gap.EndUtc == result.DesiredEndUtc); // Trailing coverage matters.
        var restarted = Service();
        await restarted.SynchronizePlanAsync();
        Assert.Null(await restarted.RunNextSegmentAsync());
        var fill = await restarted.FillGapAsync("FX-EURUSD", "EURUSD", "1m", result.DesiredStartUtc, result.DesiredEndUtc);
        Assert.True(fill.Success);
        Assert.Equal(0, fill.BarsInserted);
        using var db = fixture.CreateDbContext();
        Assert.All(db.Candles, row => { Assert.Null(row.Volume); Assert.Equal("IBKR.Midpoint", row.Source); });
        var path = Path.Combine(Path.GetTempPath(), "fx4-parquet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = path }),
                fixture.Clock, NullLogger<ParquetMarketDatasetStore>.Instance);
            await store.WriteBatchAsync(sink.Records);
            Assert.True((await store.VerifyAsync()).IsValid);
            var read = await store.ReadAsync(new DatasetQuery());
            Assert.NotEmpty(read);
            Assert.All(read, item => { Assert.Null(item.Volume); Assert.Equal("IBKR.Midpoint", item.Source); Assert.False(item.CanTriggerTrading); });
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task EmptyWeekendHistoryIsNotMisreportedAsMissingOpenSessionData()
    {
        using var fixture = new Fixture();
        fixture.Clock.UtcNow = new DateTime(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc);
        var registry = new InstrumentRegistryService(fixture, Options.Create(Settings()), fixture.Clock);
        await registry.SynchronizeConfiguredAsync();
        var service = new HistoricalBackfillService(fixture, new History(empty: true), registry,
            Options.Create(new HistoricalBackfillSettings { PacingDelayMilliseconds = 0, OneMinute = new() { LookbackDays = 1, SegmentDays = 1 } }),
            fixture.Clock, NullLogger<HistoricalBackfillService>.Instance);
        await service.SynchronizePlanAsync();
        var result = await service.RunNextSegmentAsync();
        Assert.Equal(HistoricalBackfillStatus.Completed, result!.Status);
        Assert.Equal(0, result.GapCount);
        Assert.Equal(0, result.BarsInserted);
    }

    [Fact]
    public async Task MigrationPreservesExistingStockRowsAndAllowsUnknownVolume()
    {
        using var fixture = new Fixture(ensureCreated: false);
        using var db = fixture.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        var before = db.Database.GetMigrations().Last(x => !x.EndsWith("AllowUnavailableCandleVolume"));
        await migrator.MigrateAsync(before);
        db.Candles.Add(new Candle { InstrumentId = "SPY", Symbol = "SPY", Source = "Legacy", Volume = 123m,
            TimestampUtc = fixture.Clock.UtcNow, ReceivedTimeUtc = fixture.Clock.UtcNow });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(123m, (await db.Candles.SingleAsync()).Volume);
        db.Candles.Add(new Candle { InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Source = "IBKR.Midpoint",
            Volume = null, TimestampUtc = fixture.Clock.UtcNow, ReceivedTimeUtc = fixture.Clock.UtcNow });
        await db.SaveChangesAsync();
        Assert.Null((await db.Candles.SingleAsync(x => x.Symbol == "EURUSD")).Volume);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    internal static MarketBar Bar(DateTime at) => new()
    {
        InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Timeframe = "1m", TimestampUtc = at,
        Source = "IBKR.Midpoint", Open = 1.1m, High = 1.2m, Low = 1m, Close = 1.15m, Volume = null
    };

    private static CanonicalMarketDataEvent Canonical(DateTime at) => new()
    {
        EventId = at.ToString("O"), InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Kind = MarketDataEventKind.Bar,
        Timeframe = "1m", EventTimeUtc = at, ReceivedTimeUtc = at.AddMinutes(1), Source = "IBKR.Midpoint",
        Open = 1.1m, High = 1.2m, Low = 1m, Close = 1.15m
    };

    private sealed class History(bool empty = false) : IMarketDataService
    {
        public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<MarketBar>>(empty ? [] : [Bar(request.StartUtc), Bar(request.StartUtc.AddMinutes(2))]);
        public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Sink : IMarketDatasetSink
    {
        public List<MarketDatasetRecord> Records { get; } = [];
        public ValueTask<bool> EnqueueAsync(MarketDatasetRecord record, CancellationToken cancellationToken = default)
        { Records.Add(record); return ValueTask.FromResult(true); }
    }
    private sealed class TestClock : IClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    }
    private sealed class Fixture : IDbContextFactory<TradingBotDbContext>, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<TradingBotDbContext> _options;
        public TestClock Clock { get; } = new();
        public Fixture(bool ensureCreated = true)
        {
            var connection = $"Data Source=fx4-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keeper = new(connection); _keeper.Open();
            _options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            if (ensureCreated) { using var db = CreateDbContext(); db.Database.EnsureCreated(); }
        }
        public TradingBotDbContext CreateDbContext() => new(_options);
        public void Dispose() => _keeper.Dispose();
    }
}
