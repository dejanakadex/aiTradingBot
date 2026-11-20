using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class CollectionTimingTests
{
    [Fact]
    public void HistogramCountsEntireWindowAndBoundsQuantilesWithoutHidingLongTails()
    {
        var h = new LatencyHistogram();
        for (var i = 0; i < 99000; i++) h.Record(1);
        for (var i = 0; i < 1000; i++) h.Record(2000);
        h.Record(-1); h.Record(double.NaN);
        var result = h.Take();
        Assert.Equal(100000, result.Count);
        Assert.InRange(result.P50UpperMs!.Value, 1, 1.1);
        Assert.InRange(result.P95UpperMs!.Value, 1, 1.1);
        Assert.InRange(result.P99UpperMs!.Value, 1, 1.1);
        Assert.Equal(2000, result.MaxMs);
        Assert.Equal(0, h.Take().Count);
    }

    [Fact]
    public async Task TimingScopesStayIndependentAcrossConcurrentAsyncCallsAndRestoreParent()
    {
        using var parent = new CollectionTiming();
        async Task<Dictionary<string, double>> Measure(CollectionPhase phase)
        {
            using var timing = new CollectionTiming();
            using (CollectionTiming.Measure(phase)) await Task.Delay(20);
            return timing.Snapshot();
        }
        var results = await Task.WhenAll(Measure(CollectionPhase.QualitySave), Measure(CollectionPhase.DatasetEnqueue));
        Assert.Equal("QualitySave", Assert.Single(results[0]).Key);
        Assert.Equal("DatasetEnqueue", Assert.Single(results[1]).Key);
        Assert.Empty(parent.Snapshot());
        using (CollectionTiming.Measure(CollectionPhase.Aggregates)) { }
        Assert.Equal("Aggregates", Assert.Single(parent.Snapshot()).Key);
    }

    [Fact]
    public void OutlierRetainsItsOwnPhaseAndMonotonicQueueWhenWallClockMoves()
    {
        var window = new TickLatencyWindow();
        var at = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
            window.Record(new() { EventId = "pair:" + i, EventTimeUtc = at, ReceivedTimeUtc = at.AddMilliseconds(500) },
                at.AddMilliseconds(400), TimeSpan.FromMilliseconds(1000 + i), false,
                new() { ["QualitySave"] = 999 + i }, 10);
        var result = window.Take();
        Assert.Equal(10, result.NegativeQueueClockDeltas);
        Assert.Equal(10, result.QueueMonotonic!.Count);
        Assert.Equal(0, result.QueueWallClock!.Count);
        Assert.Equal(3, result.SlowEvents.Length);
        Assert.Equal("pair:9", result.SlowEvents[0].EventId);
        Assert.Equal(1008, result.SlowEvents[0].Phases!["QualitySave"]);
        Assert.Empty(window.Take().SlowEvents);
    }

    [Fact]
    public void WorstPipelineIsIndependentOfQueueOutlierSelection()
    {
        var window = new TickLatencyWindow(); var at = DateTime.UtcNow;
        window.Record(new() { EventId = "queue", EventTimeUtc = at, ReceivedTimeUtc = at }, at.AddSeconds(1), TimeSpan.FromMilliseconds(2), false);
        window.Record(new() { EventId = "pipeline", EventTimeUtc = at, ReceivedTimeUtc = at }, at, TimeSpan.FromMilliseconds(80), false);
        var report = window.Take();
        Assert.Equal("pipeline", report.WorstPipelineEvent!.EventId);
        Assert.Equal(report.MaxPipelineMs, report.WorstPipelineEvent.PipelineMs);
        Assert.Equal("queue", report.WorstQueueEvent!.EventId);
    }

    [Fact]
    public async Task ContinuingTrickleCannotPostponeFlushAndShutdownDrainsExactlyOnce()
    {
        var store = new RecordingStore();
        using var writer = new MarketDatasetWriterHostedService(store,
            Options.Create(new DatasetStorageSettings { BatchSize = 10000, QueueCapacity = 10000, FlushIntervalSeconds = 1 }),
            NullLogger<MarketDatasetWriterHostedService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        using var produce = new CancellationTokenSource();
        var sent = new List<string>();
        var producer = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; ; i++)
                {
                    var id = i.ToString();
                    Assert.True(await writer.EnqueueAsync(new() { EventId = id }, produce.Token)); sent.Add(id);
                    await Task.Delay(20, produce.Token);
                }
            }
            catch (OperationCanceledException) when (produce.IsCancellationRequested) { }
        });
        try { await store.FirstFlush.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { produce.Cancel(); await producer; await writer.StopAsync(CancellationToken.None); }
        Assert.True(sent.Count > 1);
        Assert.Equal(sent, store.Ids.ToArray());
        Assert.False(await writer.EnqueueAsync(new() { EventId = "after-stop" }));
    }

    [Fact]
    public async Task GracefulStopFinishesInFlightWriteWithoutCancellationOrRetry()
    {
        var store = new RecordingStore { Release = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var writer = new MarketDatasetWriterHostedService(store,
            Options.Create(new DatasetStorageSettings { BatchSize = 1, QueueCapacity = 10, FlushIntervalSeconds = 1 }),
            NullLogger<MarketDatasetWriterHostedService>.Instance);
        await writer.StartAsync(CancellationToken.None);
        await writer.EnqueueAsync(new() { EventId = "pending" });
        await store.FirstFlush.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = writer.StopAsync(CancellationToken.None);
        await Task.Yield();
        store.Release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, store.WriteCalls);
        Assert.Equal("pending", Assert.Single(store.Ids));
    }

    private sealed class RecordingStore : IMarketDatasetStore
    {
        public readonly ConcurrentQueue<string> Ids = new();
        public readonly TaskCompletionSource FirstFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Release;
        public int WriteCalls;
        public async Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            FirstFlush.TrySetResult();
            if (Release != null) await Release.Task.WaitAsync(cancellationToken);
            foreach (var row in records) Ids.Enqueue(row.EventId);
            return new DatasetWriteResult(1, records.Count, 0);
        }
        public Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
