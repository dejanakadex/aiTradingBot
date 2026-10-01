using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class TickMarketDataTests
{
    [Fact]
    public async Task RealAdapterBufferPreservesBrokerTimeSizesAndReportsOverflow()
    {
        var type = typeof(MarketDataPipeline).Assembly.GetType(
            "TradingBot.Infrastructure.Services.IbkrBrokerService+TickSubscription");
        if (type == null) return; // The official API DLL is absent in the fallback build.
        var constructor = Assert.Single(type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        var subscription = Assert.IsAssignableFrom<ITickMarketDataSubscription>(constructor.Invoke(
            ["SPY-ID", "SPY", 128, (Action)(() => { })]));
        try
        {
            var timestamp = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();
            var quote = type.GetMethod("PublishQuote")!;
            var trade = type.GetMethod("Publish")!;
            quote.Invoke(subscription, [timestamp, 100d, 100.01d, 12m, 15m]);
            quote.Invoke(subscription, [timestamp, 100d, 100.01d, 12m, 15m]);
            trade.Invoke(subscription, [MarketDataEventKind.Trade, timestamp, 100.005d, 7m]);

            var events = new List<CanonicalMarketDataEvent>();
            while (subscription.Reader.TryRead(out var item)) events.Add(item);
            Assert.Equal(3, events.Count);
            Assert.Equal(new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask, MarketDataEventKind.Trade }, events.Select(x => x.Kind));
            Assert.Equal(new decimal?[] { 12m, 15m, 7m }, events.Select(x => x.Size));
            Assert.All(events, x => Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime, x.EventTimeUtc));
            Assert.All(events, x => Assert.True(x.ReceivedTimeUtc > x.EventTimeUtc));
            Assert.Equal(3, events.Select(x => x.EventId).Distinct().Count());

            for (var index = 0; index < 140; index++)
                trade.Invoke(subscription, [MarketDataEventKind.Trade, timestamp, 100d, 1m]);
            Assert.True(subscription.DroppedEvents > 0);
        }
        finally { await subscription.DisposeAsync(); }
    }

    [Fact]
    public async Task CollectsIndependentInstrumentTicksAndRecordsUnrecoverableInterruption()
    {
        var connectionString = $"Data Source=tick-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var anchor = new SqliteConnection(connectionString);
        anchor.Open();
        var dbOptions = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connectionString).Options;
        var factory = new TestFactory(dbOptions);
        using (var db = factory.CreateDbContext()) db.Database.Migrate();

        var settings = new TradingSettings
        {
            Instruments =
            [
                new InstrumentSettings { InstrumentId = "SPY-ID", Symbol = "SPY", MarketDataTimeframes = ["1m"] },
                new InstrumentSettings { InstrumentId = "QQQ-ID", Symbol = "QQQ", MarketDataTimeframes = ["1m"] }
            ]
        };
        var clock = new SystemClock();
        var tickService = new FakeTickService();
        var sink = new CapturingSink();
        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        using var pipeline = new MarketDataPipeline(
            new FakeIbkrAdapter(), bus, factory,
            new MarketDataValidator(Options.Create(settings), clock), null,
            NullLogger<MarketDataPipeline>.Instance,
            settings: Options.Create(settings), clock: clock, datasetSink: sink);
        var quality = new MarketDataQualityService(factory, Options.Create(settings), clock,
            NullLogger<MarketDataQualityService>.Instance);
        var status = new MarketDataCollectionStatusService();
        var service = new TickMarketDataHostedService([tickService], new ConnectedBroker(), pipeline,
            status, quality, factory, Options.Create(settings), Options.Create(new MarketDataCollectionSettings
            {
                MonitorIntervalSeconds = 1,
                TickHeartbeatTimeoutSeconds = 30,
                ReconnectInitialDelaySeconds = 1,
                ReconnectMaximumDelaySeconds = 1,
                RegularSessionOnly = false
            }), clock, NullLogger<TickMarketDataHostedService>.Instance);

        try
        {
            await service.StartAsync(CancellationToken.None);
            await UntilAsync(() => tickService.Subscriptions.Count == 2);
            var now = DateTime.UtcNow;
            tickService.Publish("SPY", Tick("SPY-ID", "SPY", MarketDataEventKind.Bid, 100m, 10m, now));
            tickService.Publish("QQQ", Tick("QQQ-ID", "QQQ", MarketDataEventKind.Trade, 200m, 5m, now));
            await UntilAsync(() => sink.Records.Count == 2);
            Assert.Contains(sink.Records, x => x.InstrumentId == "SPY-ID" && x.Size == 10m);
            Assert.Contains(sink.Records, x => x.InstrumentId == "QQQ-ID" && x.Size == 5m);
            Assert.All(status.GetAll(), x => Assert.Equal(MarketDataCollectionStatus.Live, x.Status));

            tickService.Complete("SPY");
            await UntilAsync(() => tickService.Subscriptions.Count == 3, 5000);
            await UntilAsync(() => HasGap(factory, "SPY-ID"), 5000);
            using var verify = factory.CreateDbContext();
            Assert.Contains(verify.TickCoverageGapRecords, x => x.InstrumentId == "SPY-ID" && x.Reason.Contains("Tick channel completed"));
            Assert.DoesNotContain(verify.TickCoverageGapRecords, x => x.InstrumentId == "QQQ-ID");
            Assert.Contains(status.GetAll(), x => x.InstrumentId == "QQQ-ID" && x.Subscribed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private static CanonicalMarketDataEvent Tick(string id, string symbol, MarketDataEventKind kind, decimal price, decimal size, DateTime now) => new()
    {
        EventId = Guid.NewGuid().ToString("N"), InstrumentId = id, Symbol = symbol, Kind = kind,
        EventTimeUtc = now, ReceivedTimeUtc = now, Source = "IBKR.TickByTick", Price = price, Size = size
    };

    private static bool HasGap(TestFactory factory, string instrumentId)
    {
        using var db = factory.CreateDbContext();
        return db.TickCoverageGapRecords.Any(x => x.InstrumentId == instrumentId);
    }

    private static async Task UntilAsync(Func<bool> predicate, int timeoutMs = 3000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private sealed class CapturingSink : IMarketDatasetSink
    {
        public ConcurrentQueue<MarketDatasetRecord> Records { get; } = new();
        public ValueTask<bool> EnqueueAsync(MarketDatasetRecord record, CancellationToken cancellationToken = default)
        {
            Records.Enqueue(record);
            return ValueTask.FromResult(true);
        }
    }

    private sealed class ConnectedBroker : IIbkrConnectionService
    {
        public ConnectionStatus Status => ConnectionStatus.Connected;
#pragma warning disable CS0067
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
#pragma warning restore CS0067
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTickService : ITickMarketDataService
    {
        public ConcurrentQueue<FakeSubscription> Subscriptions { get; } = new();
        public Task<ITickMarketDataSubscription> SubscribeTicksAsync(string instrumentId, string symbol, CancellationToken cancellationToken = default)
        {
            var subscription = new FakeSubscription(symbol);
            Subscriptions.Enqueue(subscription);
            return Task.FromResult<ITickMarketDataSubscription>(subscription);
        }
        public void Publish(string symbol, CanonicalMarketDataEvent marketEvent) => Subscriptions.Last(x => x.Symbol == symbol).Channel.Writer.TryWrite(marketEvent);
        public void Complete(string symbol) => Subscriptions.Last(x => x.Symbol == symbol).Channel.Writer.TryComplete();
    }

    private sealed class FakeSubscription(string symbol) : ITickMarketDataSubscription
    {
        public string Symbol { get; } = symbol;
        public Channel<CanonicalMarketDataEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<CanonicalMarketDataEvent>();
        public ChannelReader<CanonicalMarketDataEvent> Reader => Channel.Reader;
        public long DroppedEvents => 0;
        public ValueTask DisposeAsync() { Channel.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }

    private sealed class TestFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
    {
        public TradingBotDbContext CreateDbContext() => new(options);
    }
}
