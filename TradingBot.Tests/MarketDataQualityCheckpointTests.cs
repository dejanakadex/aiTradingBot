using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class MarketDataQualityCheckpointTests
{
    [Fact]
    public void RestartCoverageStartsAtOldestDurableSideOfEachInstrument()
    {
        var now = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var bid = new MarketDataStreamSnapshot { InstrumentId = "EURUSD", Kind = MarketDataEventKind.Bid,
            Source = "IBKR.TickByTick.BidAsk", LastReceivedTimeUtc = now };
        var checkpoints = TickMarketDataHostedService.RestartCheckpoints([
            bid, bid with { Kind = MarketDataEventKind.Ask, LastReceivedTimeUtc = now.AddSeconds(-2) },
            bid with { InstrumentId = "SPY", Kind = MarketDataEventKind.Trade, LastReceivedTimeUtc = now.AddSeconds(-1) },
            bid with { Source = "Historical", LastReceivedTimeUtc = now.AddHours(-1) }
        ]);
        Assert.Equal(now.AddSeconds(-2), checkpoints["EURUSD"]);
        Assert.Equal(now.AddSeconds(-1), checkpoints["SPY"]);
    }

    [Fact]
    public async Task PeriodicWorkerFlushesWithoutAnotherIncomingTickAndStopDrainsTail()
    {
        using var f = new Fixture(); using var service = f.Service(100);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (service.GetPersistenceStatus().PendingStreams > 0) await Task.Delay(10, deadline.Token);
            using (var db = f.CreateDbContext()) Assert.Equal(2, db.MarketDataStreamStateRecords.Single().Version);
            await service.EvaluateAsync(f.Tick(3));
        }
        finally { await service.StopAsync(CancellationToken.None); }
        using var check = f.CreateDbContext(); Assert.Equal(3, check.MarketDataStreamStateRecords.Single().Version);
    }

    [Fact]
    public async Task CheckpointAndPerEventPathsGiveIdenticalAssessmentsAndLatestViews()
    {
        using var fast = new Fixture(); using var strict = new Fixture();
        using var buffered = fast.Service(); using var immediate = strict.Service(0);
        var fastLatest = new LatestMarketDataService(); var strictLatest = new LatestMarketDataService();
        for (var i = 1; i <= 80; i++)
            foreach (var kind in new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask, MarketDataEventKind.Trade })
            {
                var tick = fast.Tick(i, kind) with { Price = 100 + i * .01m, Size = 1 };
                var left = await buffered.EvaluateAsync(tick); var right = await immediate.EvaluateAsync(tick);
                Assert.Equal(right, left);
                if (left.IsHealthy) fastLatest.Apply(tick);
                if (right.IsHealthy) strictLatest.Apply(tick);
            }
        var duplicate = fast.Tick(80, MarketDataEventKind.Trade);
        Assert.Equal(await immediate.EvaluateAsync(duplicate), await buffered.EvaluateAsync(duplicate));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(strictLatest.GetAll()),
            System.Text.Json.JsonSerializer.Serialize(fastLatest.GetAll()));
        await buffered.FlushCheckpointsAsync();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(await immediate.GetStreamsAsync()),
            System.Text.Json.JsonSerializer.Serialize(await buffered.GetStreamsAsync()));
        Assert.True(fast.Interceptor.Saves < strict.Interceptor.Saves / 10);
    }

    [Fact]
    public async Task InProcessTradingSettingChangeFailsClosedUntilRestart()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); f.Trading.Enabled = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EvaluateAsync(f.Tick(2)));
        Assert.False(service.GetPersistenceStatus().AcceptingEvents);
        Assert.False(Assert.Single(await service.GetStreamsAsync()).IsHealthy);
    }

    [Fact]
    public async Task HealthyTicksCoalesceButViewsAndOrderingUseEveryEvent()
    {
        using var f = new Fixture(); using var service = f.Service();
        for (var i = 1; i <= 100; i++) Assert.True((await service.EvaluateAsync(f.Tick(i))).IsHealthy);
        Assert.Equal(1, f.Interceptor.Saves);
        Assert.Equal(100, Assert.Single(await service.GetStreamsAsync()).Version);
        using (var db = f.CreateDbContext()) Assert.Equal(1, Assert.Single(db.MarketDataStreamStateRecords).Version);
        await service.FlushCheckpointsAsync();
        Assert.Equal(2, f.Interceptor.Saves);
        using (var db = f.CreateDbContext()) Assert.Equal(100, Assert.Single(db.MarketDataStreamStateRecords).Version);
        Assert.Equal(0, service.GetPersistenceStatus().PendingStreams);
        var duplicate = await service.EvaluateAsync(f.Tick(100));
        Assert.Equal(MarketDataQualityStatus.Duplicate, duplicate.Status);
        using (var db = f.CreateDbContext()) Assert.Single(db.MarketDataQualityIncidentRecords);
    }

    [Fact]
    public async Task BlockedCheckpointDoesNotBlockHealthyTicksOrLoseNewerState()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        f.Interceptor.BlockNext();
        var flush = service.FlushCheckpointsAsync();
        await f.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            for (var i = 3; i <= 25; i++)
                Assert.True((await service.EvaluateAsync(f.Tick(i)).WaitAsync(TimeSpan.FromSeconds(1))).IsHealthy);
            Assert.False(flush.IsCompleted);
        }
        finally { f.Interceptor.Release.TrySetResult(); }
        await flush;
        Assert.Equal(1, service.GetPersistenceStatus().PendingStreams);
        await service.FlushCheckpointsAsync();
        using var db = f.CreateDbContext();
        Assert.Equal("tick-25-Bid", Assert.Single(db.MarketDataStreamStateRecords).LastEventId);
    }

    [Fact]
    public async Task IncidentIsVisibleBeforeItsWriteCompletesAndCannotBeOverwrittenByOlderSnapshot()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        f.Interceptor.BlockNext();
        var flush = service.FlushCheckpointsAsync();
        await f.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = service.EvaluateAsync(f.Tick(2));
        // The incident is published in memory before waiting for the serialized writer.
        Assert.False(Assert.Single(await service.GetStreamsAsync()).IsHealthy);
        f.Interceptor.Release.TrySetResult();
        await flush;
        Assert.Equal(MarketDataQualityStatus.Duplicate, (await rejected).Status);
        await service.FlushCheckpointsAsync();
        using var db = f.CreateDbContext();
        Assert.Equal(MarketDataQualityStatus.Duplicate, Assert.Single(db.MarketDataStreamStateRecords).Status);
        Assert.Single(db.MarketDataQualityIncidentRecords);
    }

    [Fact]
    public async Task FailureBlocksNextEventAndRetriesPendingIncidentExactlyOnce()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1));
        f.Interceptor.FailNext = true;
        await Assert.ThrowsAsync<IOException>(() => service.EvaluateAsync(f.Tick(1)));
        Assert.False(service.GetPersistenceStatus().AcceptingEvents);
        Assert.False(Assert.Single(await service.GetStreamsAsync()).IsHealthy);
        await Assert.ThrowsAsync<IOException>(() => service.EvaluateAsync(f.Tick(2)));
        await service.FlushCheckpointsAsync();
        await service.FlushCheckpointsAsync();
        Assert.True((await service.EvaluateAsync(f.Tick(2))).IsHealthy);
        using var db = f.CreateDbContext();
        Assert.Single(db.MarketDataQualityIncidentRecords);
    }

    [Fact]
    public async Task CancelledIncidentWaitStaysBlockedUntilItsOwnCheckpointIsSaved()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        f.Interceptor.BlockNext(); var oldFlush = service.FlushCheckpointsAsync();
        await f.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var incident = service.EvaluateAsync(f.Tick(2), cancellationToken: cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => incident);
        f.Interceptor.Release.TrySetResult(); await oldFlush;
        Assert.False(service.GetPersistenceStatus().AcceptingEvents); // Older successful flush cannot acknowledge the newer incident.
        await Assert.ThrowsAsync<IOException>(() => service.EvaluateAsync(f.Tick(3)));
        await service.FlushCheckpointsAsync();
        using (var db = f.CreateDbContext()) Assert.Single(db.MarketDataQualityIncidentRecords);
        Assert.True((await service.EvaluateAsync(f.Tick(3))).IsHealthy);
    }

    [Fact]
    public async Task MonotonicLagBoundStopsAcceptanceEvenIfWallClockMovesBackwards()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        f.Time.Advance(6000); f.UtcNow = f.UtcNow.AddHours(-1);
        Assert.False(service.GetPersistenceStatus().AcceptingEvents);
        Assert.True(service.GetPersistenceStatus().MaximumObservedPendingMilliseconds >= 6000);
        await Assert.ThrowsAsync<IOException>(() => service.EvaluateAsync(f.Tick(3)));
        Assert.False(Assert.Single(await service.GetStreamsAsync()).IsHealthy);
        await service.FlushCheckpointsAsync();
        Assert.True(service.GetPersistenceStatus().AcceptingEvents);
        Assert.True((await service.EvaluateAsync(f.Tick(3))).IsHealthy);
    }

    [Fact]
    public async Task GracefulStopDrainsAndRestartUsesDurableWatermark()
    {
        using var f = new Fixture(); using var first = f.Service();
        await first.EvaluateAsync(f.Tick(1)); await first.EvaluateAsync(f.Tick(10));
        await first.StopAsync(CancellationToken.None);
        Assert.False(first.GetPersistenceStatus().AcceptingEvents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.EvaluateAsync(f.Tick(11)));
        using var restarted = f.Service();
        Assert.Equal(MarketDataQualityStatus.OutOfOrder, (await restarted.EvaluateAsync(f.Tick(9))).Status);
        Assert.Equal(f.Tick(10).ReceivedTimeUtc, (await first.GetStreamsAsync()).Single().LastReceivedTimeUtc);
    }

    [Fact]
    public async Task UncleanRestartExposesLastDurableWatermarkRatherThanClaimingUnsavedTail()
    {
        using var f = new Fixture(); using var first = f.Service();
        await first.EvaluateAsync(f.Tick(1)); await first.EvaluateAsync(f.Tick(10));
        using var restarted = f.Service();
        var checkpoint = Assert.Single(await restarted.GetStreamsAsync());
        Assert.Equal(f.Tick(1).ReceivedTimeUtc, checkpoint.LastReceivedTimeUtc);
        Assert.Equal(1, checkpoint.Version); // Tick collector uses this older time to record conservative restart coverage.
    }

    [Theory]
    [InlineData(true, TradingOperatingMode.AnalysisOnly, false)]
    [InlineData(false, TradingOperatingMode.PaperTrading, false)]
    [InlineData(false, TradingOperatingMode.AnalysisOnly, true)]
    public async Task TradingConfigurationsKeepPerEventDurability(bool enabled, TradingOperatingMode mode, bool forex)
    {
        using var f = new Fixture(); f.Trading.Enabled = enabled; f.Trading.OperatingMode = mode; f.Trading.ForexPaperExecutionEnabled = forex;
        using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        Assert.False(service.GetPersistenceStatus().CheckpointsEnabled);
        Assert.Equal(2, f.Interceptor.Saves);
        using var db = f.CreateDbContext(); Assert.Equal(2, Assert.Single(db.MarketDataStreamStateRecords).Version);
    }

    [Fact]
    public async Task ExplicitSequencesAndNonLiveSourcesStillPersistEveryEvent()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1) with { Sequence = 1 });
        await service.EvaluateAsync(f.Tick(2) with { Sequence = 2 });
        await service.EvaluateAsync(f.Tick(3) with { Source = "Replay" });
        Assert.Equal(3, f.Interceptor.Saves);
        Assert.Equal(0, service.GetPersistenceStatus().PendingStreams);
    }

    [Fact]
    public async Task DifferentKindsAreIsolatedAndConcurrentFlushesDoNotOverwriteNewerVersion()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(1, MarketDataEventKind.Ask));
        await service.EvaluateAsync(f.Tick(2));
        f.Interceptor.BlockNext(); var a = service.FlushCheckpointsAsync();
        await f.Interceptor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var b = service.FlushCheckpointsAsync();
        await service.EvaluateAsync(f.Tick(3));
        f.Interceptor.Release.TrySetResult(); await Task.WhenAll(a, b);
        await service.FlushCheckpointsAsync();
        using var db = f.CreateDbContext();
        Assert.Equal(3, db.MarketDataStreamStateRecords.Single(x => x.Kind == MarketDataEventKind.Bid).Version);
        Assert.Equal(1, db.MarketDataStreamStateRecords.Single(x => x.Kind == MarketDataEventKind.Ask).Version);
    }

    [Fact]
    public async Task ExternalWriterConflictFailsClosedWithoutOverwritingDatabase()
    {
        using var f = new Fixture(); using var service = f.Service();
        await service.EvaluateAsync(f.Tick(1)); await service.EvaluateAsync(f.Tick(2));
        using (var db = f.CreateDbContext())
        {
            var record = db.MarketDataStreamStateRecords.Single(); record.Version = 100; record.LastEventId = "external"; db.SaveChanges();
        }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => service.FlushCheckpointsAsync());
        await Assert.ThrowsAsync<IOException>(() => service.EvaluateAsync(f.Tick(3)));
        using var check = f.CreateDbContext(); Assert.Equal("external", check.MarketDataStreamStateRecords.Single().LastEventId);
    }

    private sealed class TestTime : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public void Advance(long ms) => Interlocked.Add(ref _timestamp, ms);
    }
    private sealed class WriteInterceptor : SaveChangesInterceptor
    {
        public int Saves;
        public bool FailNext;
        private bool _block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNext() { _block = true; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!data.Context!.ChangeTracker.HasChanges()) return result;
            Interlocked.Increment(ref Saves);
            if (FailNext) { FailNext = false; throw new IOException("Injected checkpoint failure"); }
            if (_block) { _block = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            return result;
        }
    }
    private sealed class Fixture : IDbContextFactory<TradingBotDbContext>, IClock, IDisposable
    {
        private readonly SqliteConnection _keeper;
        private readonly DbContextOptions<TradingBotDbContext> _options;
        public readonly WriteInterceptor Interceptor = new();
        public readonly TestTime Time = new();
        public DateTime UtcNow { get; set; } = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
        public readonly TradingSettings Trading = new()
        {
            Instruments = [new InstrumentSettings { InstrumentId = "SPY", Symbol = "SPY", Enabled = true }]
        };
        public Fixture()
        {
            var connection = $"Data Source=checkpoint-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keeper = new(connection); _keeper.Open();
            _options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).AddInterceptors(Interceptor).Options;
            using var db = CreateDbContext(); db.Database.EnsureCreated();
        }
        public MarketDataQualityService Service(int interval = 1000) => new(this, Options.Create(Trading), this,
            NullLogger<MarketDataQualityService>.Instance, collection: Options.Create(new MarketDataCollectionSettings
            { QualityStateCheckpointMilliseconds = interval, QualityStateMaximumLagMilliseconds = 5000 }), timeProvider: Time);
        public CanonicalMarketDataEvent Tick(int index, MarketDataEventKind kind = MarketDataEventKind.Bid) => new()
        {
            EventId = $"tick-{index}-{kind}", InstrumentId = "SPY", Symbol = "SPY", Kind = kind, Price = 100,
            EventTimeUtc = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(index),
            ReceivedTimeUtc = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc).AddMilliseconds(index),
            Source = "IBKR.TickByTick.BidAsk"
        };
        public TradingBotDbContext CreateDbContext() => new(_options);
        public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        public void Dispose() => _keeper.Dispose();
    }
}
