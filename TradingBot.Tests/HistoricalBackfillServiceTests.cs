using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class HistoricalBackfillServiceTests
    {
        [Fact]
        public async Task RestartResumesCheckpoint_DeduplicatesCandles_AndCompletesWithGapReport()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc));
            var trading = BuildTradingSettings();
            var registry = new InstrumentRegistryService(factory, Options.Create(trading), clock);
            await registry.SynchronizeConfiguredAsync();
            var marketData = new RecordingMarketDataService(request =>
            {
                var interval = TimeSpan.FromMinutes(1);
                return new[]
                {
                    Bar(request, request.StartUtc),
                    Bar(request, request.StartUtc.Add(interval * 2))
                };
            });
            var settings = BackfillSettings(lookbackDays: 2, segmentDays: 1);
            var firstProcess = CreateService(factory, marketData, registry, settings, clock);

            await firstProcess.SynchronizePlanAsync();
            var firstSegment = await firstProcess.RunNextSegmentAsync();

            Assert.NotNull(firstSegment);
            Assert.Equal(HistoricalBackfillStatus.Pending, firstSegment!.Status);
            var checkpoint = firstSegment.NextSegmentEndUtc;
            Assert.Equal(2, firstSegment.BarsInserted);

            var restarted = CreateService(factory, marketData, registry, settings, clock);
            await restarted.SynchronizePlanAsync();
            var completed = await restarted.RunNextSegmentAsync();

            Assert.NotNull(completed);
            Assert.Equal(checkpoint, marketData.Requests[1].EndUtc);
            Assert.Equal(HistoricalBackfillStatus.CompletedWithGaps, completed!.Status);
            Assert.Equal(2, completed.CompletedSegments);
            Assert.True(completed.GapCount > 0);
            Assert.Equal(InstrumentOnboardingStatus.Collecting, (await registry.GetAsync("US-STK-SPY-SMART"))!.Status);

            await using var db = factory.CreateDbContext();
            Assert.Equal(4, await db.Candles.CountAsync());
            Assert.Equal(2, await db.HistoricalBackfillSegmentRecords.CountAsync(item => item.Status == HistoricalBackfillSegmentStatus.Completed));
            Assert.True(await db.HistoricalDataGapRecords.AnyAsync());
        }

        [Fact]
        public async Task ExistingCandleIsCountedAsDuplicateInsteadOfInsertedAgain()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc));
            var trading = BuildTradingSettings();
            var registry = new InstrumentRegistryService(factory, Options.Create(trading), clock);
            await registry.SynchronizeConfiguredAsync();
            var settings = BackfillSettings(1, 1);
            HistoricalBarRequest? captured = null;
            var marketData = new RecordingMarketDataService(request =>
            {
                captured = request;
                return new[] { Bar(request, request.StartUtc) };
            });
            var service = CreateService(factory, marketData, registry, settings, clock);
            await service.SynchronizePlanAsync();

            var job = Assert.Single(await service.GetJobsAsync());
            await using (var db = factory.CreateDbContext())
            {
                db.Candles.Add(new Candle
                {
                    InstrumentId = job.InstrumentId,
                    Symbol = job.Symbol,
                    Timeframe = Timeframe.OneMinute,
                    TimestampUtc = job.DesiredStartUtc,
                    ReceivedTimeUtc = clock.UtcNow,
                    Source = "test",
                    IsFinal = true,
                    QualityStatus = MarketDataQualityStatus.Healthy,
                    Open = 100m,
                    High = 101m,
                    Low = 99m,
                    Close = 100m,
                    Volume = 1m
                });
                await db.SaveChangesAsync();
            }

            var result = await service.RunNextSegmentAsync();

            Assert.NotNull(captured);
            Assert.NotNull(result);
            Assert.Equal(0, result!.BarsInserted);
            Assert.Equal(1, result.DuplicateBars);
            await using var verification = factory.CreateDbContext();
            Assert.Equal(1, await verification.Candles.CountAsync());
        }

        [Fact]
        public async Task RetryStateSurvivesAndExhaustionFaultsInstrument()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(new DateTime(2026, 9, 22, 20, 0, 0, DateTimeKind.Utc));
            var trading = BuildTradingSettings();
            var registry = new InstrumentRegistryService(factory, Options.Create(trading), clock);
            await registry.SynchronizeConfiguredAsync();
            var marketData = new RecordingMarketDataService(_ => throw new InvalidOperationException("IBKR pacing violation"));
            var settings = BackfillSettings(1, 1);
            settings.MaximumAttemptsPerSegment = 2;
            settings.InitialRetryDelaySeconds = 1;
            var service = CreateService(factory, marketData, registry, settings, clock);
            await service.SynchronizePlanAsync();

            var retry = await service.RunNextSegmentAsync();
            Assert.Equal(HistoricalBackfillStatus.RetryPending, retry!.Status);
            Assert.Equal(1, retry.FailedAttempts);

            clock.UtcNow = clock.UtcNow.AddSeconds(1);
            var restarted = CreateService(factory, marketData, registry, settings, clock);
            var faulted = await restarted.RunNextSegmentAsync();

            Assert.Equal(HistoricalBackfillStatus.Faulted, faulted!.Status);
            Assert.Equal(2, faulted.FailedAttempts);
            Assert.Equal(InstrumentOnboardingStatus.Faulted, (await registry.GetAsync("US-STK-SPY-SMART"))!.Status);
        }

        private static HistoricalBackfillService CreateService(
            IDbContextFactory<TradingBotDbContext> factory,
            IMarketDataService marketData,
            IInstrumentRegistryService registry,
            HistoricalBackfillSettings settings,
            IClock clock) => new(
                factory,
                marketData,
                registry,
                Options.Create(settings),
                clock,
                NullLogger<HistoricalBackfillService>.Instance);

        private static HistoricalBackfillSettings BackfillSettings(int lookbackDays, int segmentDays) => new()
        {
            PacingDelayMilliseconds = 0,
            OneMinute = new HistoricalBackfillTimeframeSettings { LookbackDays = lookbackDays, SegmentDays = segmentDays },
            FiveMinutes = new HistoricalBackfillTimeframeSettings { LookbackDays = 1, SegmentDays = 1 },
            FifteenMinutes = new HistoricalBackfillTimeframeSettings { LookbackDays = 1, SegmentDays = 1 }
        };

        private static TradingSettings BuildTradingSettings() => new()
        {
            Instruments = new[]
            {
                new InstrumentSettings
                {
                    InstrumentId = "US-STK-SPY-SMART",
                    Symbol = "SPY",
                    Exchange = "SMART",
                    Currency = "USD",
                    SecurityType = "STK",
                    Enabled = true,
                    TradingEnabled = false,
                    AllowedDirections = new[] { TradeDirection.Long, TradeDirection.Short },
                    StrategyIds = new[] { "deterministic-patterns" },
                    MarketDataTimeframes = new[] { "1m" }
                }
            }
        };

        private static MarketBar Bar(HistoricalBarRequest request, DateTime at) => new()
        {
            InstrumentId = request.InstrumentId,
            Symbol = request.Symbol,
            Timeframe = request.Timeframe,
            TimestampUtc = at,
            ReceivedTimeUtc = request.EndUtc,
            Source = "test",
            IsFinal = true,
            Open = 100m,
            High = 101m,
            Low = 99m,
            Close = 100m,
            Volume = 1000m
        };

        private static SqliteConnection CreateDatabase(out IDbContextFactory<TradingBotDbContext> factory)
        {
            var connectionString = $"Data Source=historical-backfill-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connectionString);
            keeper.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connectionString).Options;
            factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return keeper;
        }

        private sealed class RecordingMarketDataService : IMarketDataService
        {
            private readonly Func<HistoricalBarRequest, IEnumerable<MarketBar>> _response;

            public RecordingMarketDataService(Func<HistoricalBarRequest, IEnumerable<MarketBar>> response) => _response = response;

            public List<HistoricalBarRequest> Requests { get; } = new();

            public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default)
            {
                Requests.Add(request);
                return Task.FromResult(_response(request));
            }

            public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class MutableClock : IClock
        {
            public MutableClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; set; }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBotDbContext> _options;
            public SimpleDbContextFactory(DbContextOptions<TradingBotDbContext> options) => _options = options;
            public TradingBotDbContext CreateDbContext() => new(_options);
            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(CreateDbContext());
        }
    }
}
