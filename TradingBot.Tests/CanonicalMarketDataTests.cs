using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

namespace TradingBot.Tests
{
    public sealed class CanonicalMarketDataTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 23, 14, 35, 0, DateTimeKind.Utc);

        [Fact]
        public async Task QualityStatePersistsGapOutOfOrderStaleAndNonFinalEvents()
        {
            using var database = CreateDatabase(out var factory);
            var service = CreateQualityService(factory);

            var first = await service.EvaluateAsync(Bar("bar-1", NowUtc.AddMinutes(-2), sequence: 1));
            var gap = await service.EvaluateAsync(Bar("bar-3", NowUtc, sequence: 3));
            var outOfOrder = await service.EvaluateAsync(Bar("bar-2", NowUtc.AddMinutes(-1), sequence: 2));
            var stale = await service.EvaluateAsync(Tick("stale-bid", MarketDataEventKind.Bid, NowUtc.AddMinutes(-1), NowUtc, 100m));
            var nonFinal = await service.EvaluateAsync(Tick("non-final-ask", MarketDataEventKind.Ask, NowUtc, NowUtc, 100.02m) with { IsFinal = false });

            Assert.True(first.IsHealthy);
            Assert.Equal(MarketDataQualityStatus.GapDetected, gap.Status);
            Assert.True(gap.CanPersist);
            Assert.False(gap.CanTriggerTrading);
            Assert.Equal(MarketDataQualityStatus.OutOfOrder, outOfOrder.Status);
            Assert.Equal(MarketDataQualityStatus.Stale, stale.Status);
            Assert.Equal(MarketDataQualityStatus.NonFinal, nonFinal.Status);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(4, await db.MarketDataQualityIncidentRecords.CountAsync());
            var barState = await db.MarketDataStreamStateRecords.SingleAsync(item => item.Kind == MarketDataEventKind.Bar);
            Assert.Equal(MarketDataQualityStatus.OutOfOrder, barState.Status);
            Assert.Equal(3, barState.LastSequence);
            Assert.False(barState.IsHealthy);
            Assert.Equal(4, (await service.GetRecentIncidentsAsync()).Count);
        }

        [Fact]
        public async Task RestartUsesPersistedStreamPositionForOrderingChecks()
        {
            using var database = CreateDatabase(out var factory);
            var firstProcess = CreateQualityService(factory);
            Assert.True((await firstProcess.EvaluateAsync(Bar("bar-10", NowUtc, sequence: 10))).IsHealthy);

            var restartedProcess = CreateQualityService(factory);
            var result = await restartedProcess.EvaluateAsync(Bar("bar-9", NowUtc.AddMinutes(-1), sequence: 9));

            Assert.Equal(MarketDataQualityStatus.OutOfOrder, result.Status);
            var state = Assert.Single(await restartedProcess.GetStreamsAsync());
            Assert.Equal(10, state.LastSequence);
        }

        [Fact]
        public void LatestViewTracksBidAskTradeAndFiveSecondAggregates()
        {
            var latest = new LatestMarketDataService();
            latest.Apply(Tick("bid", MarketDataEventKind.Bid, NowUtc, NowUtc, 100m));
            latest.Apply(Tick("ask", MarketDataEventKind.Ask, NowUtc, NowUtc, 100.04m));
            latest.Apply(Tick("trade-1", MarketDataEventKind.Trade, NowUtc.AddSeconds(1), NowUtc.AddSeconds(1), 100.01m, 5m));
            latest.Apply(Tick("trade-2", MarketDataEventKind.Trade, NowUtc.AddSeconds(2), NowUtc.AddSeconds(2), 100.03m, 7m));

            var snapshot = latest.Get("SPY")!;
            Assert.NotNull(snapshot);
            Assert.Equal(0.04m, snapshot.Spread);
            Assert.Equal(100.03m, snapshot.LastTrade);
            var fiveSeconds = Assert.Single(snapshot.ShortAggregates, item => item.IntervalSeconds == 5);
            Assert.Equal(100.01m, fiveSeconds.Open);
            Assert.Equal(100.03m, fiveSeconds.High);
            Assert.Equal(12m, fiveSeconds.Volume);
            Assert.Equal(2, fiveSeconds.TradeCount);
            Assert.Contains(snapshot.ShortAggregates, item => item.IntervalSeconds == 15);
        }

        [Fact]
        public async Task GapBarIsPersistedForResearchButNeverPublishedToTradingPipeline()
        {
            using var database = CreateDatabase(out var factory);
            var settings = Settings();
            var clock = new FixedClock(NowUtc);
            var eventBus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
            var quality = new MarketDataQualityService(factory, Options.Create(settings), clock, NullLogger<MarketDataQualityService>.Instance);
            using var pipeline = new MarketDataPipeline(
                new FakeIbkrAdapter(),
                eventBus,
                factory,
                new MarketDataValidator(Options.Create(settings), clock),
                null,
                NullLogger<MarketDataPipeline>.Instance,
                qualityService: quality,
                latestMarketData: new LatestMarketDataService(),
                settings: Options.Create(settings),
                clock: clock);

            await pipeline.ProcessMarketBarAsync(MarketBar(NowUtc.AddMinutes(-2), sequence: 1), instrumentId: "US-STK-SPY-SMART");
            Assert.True(eventBus.CandleReader.TryRead(out var first));
            Assert.Equal("US-STK-SPY-SMART", first.InstrumentId);

            await pipeline.ProcessMarketBarAsync(MarketBar(NowUtc, sequence: 3), instrumentId: "US-STK-SPY-SMART");

            Assert.False(eventBus.CandleReader.TryRead(out _));
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(2, await db.Candles.CountAsync());
            var gapCandle = await db.Candles.SingleAsync(item => item.TimestampUtc == NowUtc);
            Assert.Equal(MarketDataQualityStatus.GapDetected, gapCandle.QualityStatus);
            Assert.Equal("US-STK-SPY-SMART", gapCandle.InstrumentId);
            Assert.True(gapCandle.IsFinal);
        }

        [Fact]
        public async Task BrokerBidAskAndTradeEventsFlowThroughCanonicalQualityGate()
        {
            using var database = CreateDatabase(out var factory);
            var settings = Settings();
            var clock = new FixedClock(NowUtc);
            var adapter = new FakeIbkrAdapter();
            var latest = new LatestMarketDataService();
            using var pipeline = new MarketDataPipeline(
                adapter,
                new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance),
                factory,
                new MarketDataValidator(Options.Create(settings), clock),
                null,
                NullLogger<MarketDataPipeline>.Instance,
                qualityService: new MarketDataQualityService(factory, Options.Create(settings), clock, NullLogger<MarketDataQualityService>.Instance),
                latestMarketData: latest,
                settings: Options.Create(settings),
                clock: clock);

            await adapter.SimulateMarketDataEventAsync(Tick("broker-bid", MarketDataEventKind.Bid, NowUtc, NowUtc, 100m) with { InstrumentId = "SPY" });
            await adapter.SimulateMarketDataEventAsync(Tick("broker-ask", MarketDataEventKind.Ask, NowUtc, NowUtc, 100.02m) with { InstrumentId = "SPY" });
            await adapter.SimulateMarketDataEventAsync(Tick("broker-trade", MarketDataEventKind.Trade, NowUtc, NowUtc, 100.01m, 3m) with { InstrumentId = "SPY" });

            var snapshot = latest.Get("US-STK-SPY-SMART")!;
            Assert.Equal(0.02m, snapshot.Spread);
            Assert.Equal(100.01m, snapshot.LastTrade);
            Assert.Equal(3, (await new MarketDataQualityService(factory, Options.Create(settings), clock, NullLogger<MarketDataQualityService>.Instance).GetStreamsAsync()).Count);
        }

        [Fact]
        public async Task CanonicalTickIsSentToDatasetWithoutCreatingOperationalCandle()
        {
            using var database = CreateDatabase(out var factory);
            var settings = Settings();
            var clock = new FixedClock(NowUtc);
            var sink = new RecordingDatasetSink();
            using var pipeline = new MarketDataPipeline(
                new FakeIbkrAdapter(),
                new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance),
                factory,
                new MarketDataValidator(Options.Create(settings), clock),
                null,
                NullLogger<MarketDataPipeline>.Instance,
                qualityService: new MarketDataQualityService(factory, Options.Create(settings), clock, NullLogger<MarketDataQualityService>.Instance),
                latestMarketData: new LatestMarketDataService(),
                settings: Options.Create(settings),
                clock: clock,
                datasetSink: sink);

            var assessment = await pipeline.ProcessCanonicalEventAsync(
                Tick("dataset-bid", MarketDataEventKind.Bid, NowUtc, NowUtc, 100m));

            Assert.True(assessment.IsHealthy);
            var stored = Assert.Single(sink.Records);
            Assert.Equal("dataset-bid", stored.EventId);
            Assert.Equal("bid", stored.DataType);
            Assert.Equal("Healthy", stored.QualityStatus);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Empty(await db.Candles.ToListAsync());
        }

        [Fact]
        public async Task MigrationCreatesQualityTablesAndCanonicalCandleColumns()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"tradingbot-market-data-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";
            try
            {
                var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connectionString).Options;
                await using (var db = new TradingBotDbContext(options))
                {
                    await db.Database.MigrateAsync();
                }

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var tables = connection.CreateCommand();
                tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('MarketDataStreamStateRecords', 'MarketDataQualityIncidentRecords');";
                Assert.Equal(2L, (long)(await tables.ExecuteScalarAsync())!);
                await using var columns = connection.CreateCommand();
                columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Candles') WHERE name IN ('InstrumentId', 'ReceivedTimeUtc', 'Source', 'IsFinal', 'QualityStatus');";
                Assert.Equal(5L, (long)(await columns.ExecuteScalarAsync())!);
            }
            finally
            {
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        private static MarketDataQualityService CreateQualityService(IDbContextFactory<TradingBotDbContext> factory) => new(
            factory,
            Options.Create(Settings()),
            new FixedClock(NowUtc),
            NullLogger<MarketDataQualityService>.Instance);

        private static TradingSettings Settings() => new()
        {
            MaximumCandleAgeSeconds = 300,
            MaximumQuoteAgeSeconds = 5,
            Instruments =
            [
                new InstrumentSettings
                {
                    InstrumentId = "US-STK-SPY-SMART",
                    Symbol = "SPY",
                    Enabled = true,
                    TradingEnabled = false,
                    AllowedDirections = [TradeDirection.Long, TradeDirection.Short],
                    StrategyIds = ["deterministic-patterns"],
                    MarketDataTimeframes = ["1m", "5m", "15m"]
                }
            ]
        };

        private static CanonicalMarketDataEvent Bar(string eventId, DateTime eventTime, long sequence) => new()
        {
            EventId = eventId,
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            Kind = MarketDataEventKind.Bar,
            EventTimeUtc = eventTime,
            ReceivedTimeUtc = NowUtc,
            Source = "Test",
            Sequence = sequence,
            IsFinal = true,
            Timeframe = "1m",
            Open = 100m,
            High = 101m,
            Low = 99m,
            Close = 100.5m,
            Volume = 1000m
        };

        private static CanonicalMarketDataEvent Tick(
            string eventId,
            MarketDataEventKind kind,
            DateTime eventTime,
            DateTime receivedTime,
            decimal price,
            decimal size = 1m) => new()
        {
            EventId = eventId,
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            Kind = kind,
            EventTimeUtc = eventTime,
            ReceivedTimeUtc = receivedTime,
            Source = "Test",
            IsFinal = true,
            Price = price,
            Size = size
        };

        private static MarketBar MarketBar(DateTime timestamp, long sequence) => new()
        {
            Symbol = "SPY",
            Timeframe = "1m",
            TimestampUtc = timestamp,
            ReceivedTimeUtc = NowUtc,
            Sequence = sequence,
            IsFinal = true,
            Open = 100m,
            High = 101m,
            Low = 99m,
            Close = 100.5m,
            Volume = 1000m
        };

        private static SqliteConnection CreateDatabase(out IDbContextFactory<TradingBotDbContext> factory)
        {
            var connectionString = $"Data Source=canonical-market-data-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connectionString);
            keeper.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connectionString).Options;
            factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return keeper;
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBotDbContext> _options;
            public SimpleDbContextFactory(DbContextOptions<TradingBotDbContext> options) => _options = options;
            public TradingBotDbContext CreateDbContext() => new(_options);
            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(CreateDbContext());
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }

        private sealed class RecordingDatasetSink : IMarketDatasetSink
        {
            public List<MarketDatasetRecord> Records { get; } = new();

            public ValueTask<bool> EnqueueAsync(MarketDatasetRecord record, CancellationToken cancellationToken = default)
            {
                Records.Add(record);
                return ValueTask.FromResult(true);
            }
        }
    }
}
