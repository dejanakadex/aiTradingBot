using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class InstrumentRegistryServiceTests
    {
        private static readonly DateTime InitialUtc = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task SynchronizeConfiguredAsync_AddsEnabledAndDisabledInstrumentsIdempotently()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(InitialUtc);
            var settings = BuildSettings(
                Instrument("US-STK-SPY-SMART", "SPY", enabled: true, tradingEnabled: false),
                Instrument("US-STK-QQQ-SMART", "QQQ", enabled: false, tradingEnabled: false));
            var service = CreateService(factory, settings, clock);

            var first = await service.SynchronizeConfiguredAsync();

            Assert.Equal(2, first.Added);
            Assert.Equal(0, first.Updated);
            var spy = Assert.Single(first.Instruments, item => item.Symbol == "SPY");
            var qqq = Assert.Single(first.Instruments, item => item.Symbol == "QQQ");
            Assert.Equal(InstrumentOnboardingStatus.BackfillPending, spy.Status);
            Assert.True(spy.CollectionEnabled);
            Assert.False(spy.CanSubmitPaperOrders);
            Assert.False(spy.CanSubmitLiveOrders);
            Assert.Equal(InstrumentOnboardingStatus.Disabled, qqq.Status);
            Assert.False(qqq.CollectionEnabled);

            clock.Advance(TimeSpan.FromMinutes(5));
            var second = await service.SynchronizeConfiguredAsync();

            Assert.Equal(0, second.Added);
            Assert.Equal(0, second.Updated);
            Assert.Equal(0, second.Disabled);
            Assert.Equal(2, second.Unchanged);
            Assert.Equal(spy.Version, Assert.Single(second.Instruments, item => item.Symbol == "SPY").Version);
            Assert.Equal(spy.UpdatedAtUtc, Assert.Single(second.Instruments, item => item.Symbol == "SPY").UpdatedAtUtc);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(2, await db.InstrumentStatusTransitionRecords.CountAsync());
        }

        [Fact]
        public async Task RestartPreservesProgressAndDataConfigurationChangeResetsOnboarding()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(InitialUtc);
            var instrument = Instrument("US-STK-SPY-SMART", "SPY", enabled: true, tradingEnabled: true);
            var settings = BuildSettings(instrument);
            var firstProcess = CreateService(factory, settings, clock);
            await firstProcess.SynchronizeConfiguredAsync();
            var registered = Assert.Single(await firstProcess.GetAllAsync());
            var withContract = await firstProcess.SetBrokerContractAsync(registered.InstrumentId, registered.Version, 756733, "ARCA");
            var backfilling = await firstProcess.TransitionAsync(
                registered.InstrumentId,
                InstrumentOnboardingStatus.BackfillPending,
                InstrumentOnboardingStatus.Backfilling,
                "Backfill worker claimed instrument.",
                "Test");

            clock.Advance(TimeSpan.FromMinutes(1));
            var restartedProcess = CreateService(factory, settings, clock);
            var restartSync = await restartedProcess.SynchronizeConfiguredAsync();

            Assert.Equal(1, restartSync.Unchanged);
            var afterRestart = Assert.Single(restartSync.Instruments);
            Assert.Equal(InstrumentOnboardingStatus.Backfilling, afterRestart.Status);
            Assert.Equal(withContract.BrokerContractId, afterRestart.BrokerContractId);
            Assert.Equal(backfilling.Version, afterRestart.Version);

            instrument.MarketDataTimeframes = new[] { "1m", "5m" };
            clock.Advance(TimeSpan.FromMinutes(1));
            var changed = await restartedProcess.SynchronizeConfiguredAsync();

            Assert.Equal(1, changed.Updated);
            var reset = Assert.Single(changed.Instruments);
            Assert.Equal(InstrumentOnboardingStatus.BackfillPending, reset.Status);
            Assert.Null(reset.BrokerContractId);
            Assert.Empty(reset.BrokerPrimaryExchange);
            Assert.Contains("configuration changed", reset.StatusReason);
        }

        [Fact]
        public async Task LiveEnabledRequiresSequentialReadinessAndExplicitTradingRequest()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(InitialUtc);
            var instrument = Instrument("US-STK-SPY-SMART", "SPY", enabled: true, tradingEnabled: false);
            var settings = BuildSettings(instrument);
            var service = CreateService(factory, settings, clock);
            await service.SynchronizeConfiguredAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransitionAsync(
                instrument.InstrumentId,
                InstrumentOnboardingStatus.BackfillPending,
                InstrumentOnboardingStatus.LiveEnabled,
                "Unsafe shortcut.",
                "Test"));

            await AdvanceToPaperReadyAsync(service, instrument.InstrumentId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransitionAsync(
                instrument.InstrumentId,
                InstrumentOnboardingStatus.PaperReady,
                InstrumentOnboardingStatus.LiveEnabled,
                "Try live without configuration permission.",
                "Test"));

            instrument.TradingEnabled = true;
            var permissionSync = await service.SynchronizeConfiguredAsync();
            var paperReady = Assert.Single(permissionSync.Instruments);
            Assert.Equal(InstrumentOnboardingStatus.PaperReady, paperReady.Status);
            Assert.True(paperReady.TradingRequested);
            Assert.True(paperReady.CanSubmitPaperOrders);
            Assert.False(paperReady.CanSubmitLiveOrders);

            var live = await service.TransitionAsync(
                instrument.InstrumentId,
                InstrumentOnboardingStatus.PaperReady,
                InstrumentOnboardingStatus.LiveEnabled,
                "Manual live approval after paper readiness.",
                "Test");

            Assert.True(live.CanSubmitLiveOrders);
        }

        [Fact]
        public async Task RemovedInstrumentIsRetainedForAuditButDisabled()
        {
            using var database = CreateDatabase(out var factory);
            var clock = new MutableClock(InitialUtc);
            var spy = Instrument("US-STK-SPY-SMART", "SPY", enabled: true, tradingEnabled: true);
            var qqq = Instrument("US-STK-QQQ-SMART", "QQQ", enabled: true, tradingEnabled: false);
            var settings = BuildSettings(spy, qqq);
            var service = CreateService(factory, settings, clock);
            await service.SynchronizeConfiguredAsync();

            settings.Instruments = new[] { qqq };
            clock.Advance(TimeSpan.FromMinutes(1));
            var result = await service.SynchronizeConfiguredAsync();

            Assert.Equal(1, result.Disabled);
            var removed = Assert.Single(result.Instruments, item => item.InstrumentId == spy.InstrumentId);
            Assert.Equal(InstrumentOnboardingStatus.Disabled, removed.Status);
            Assert.False(removed.ConfiguredEnabled);
            Assert.False(removed.TradingRequested);
            Assert.False(removed.CanSubmitLiveOrders);
            Assert.Contains("no longer present", removed.StatusReason);
        }

        [Fact]
        public async Task MigrationCreatesRegistryAndTransitionTables()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"tradingbot-registry-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Pooling=False";
            try
            {
                var options = new DbContextOptionsBuilder<TradingBotDbContext>()
                    .UseSqlite(connectionString)
                    .Options;
                await using (var db = new TradingBotDbContext(options))
                {
                    await db.Database.MigrateAsync();
                }

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('InstrumentRegistryRecords', 'InstrumentStatusTransitionRecords');";
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
            }
            finally
            {
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }

        private static async Task AdvanceToPaperReadyAsync(IInstrumentRegistryService service, string instrumentId)
        {
            var transitions = new[]
            {
                (InstrumentOnboardingStatus.BackfillPending, InstrumentOnboardingStatus.Backfilling),
                (InstrumentOnboardingStatus.Backfilling, InstrumentOnboardingStatus.Collecting),
                (InstrumentOnboardingStatus.Collecting, InstrumentOnboardingStatus.WarmingUp),
                (InstrumentOnboardingStatus.WarmingUp, InstrumentOnboardingStatus.ResearchReady),
                (InstrumentOnboardingStatus.ResearchReady, InstrumentOnboardingStatus.ShadowReady),
                (InstrumentOnboardingStatus.ShadowReady, InstrumentOnboardingStatus.PaperReady)
            };

            foreach (var (from, to) in transitions)
            {
                await service.TransitionAsync(instrumentId, from, to, $"Test transition to {to}.", "Test");
            }
        }

        private static InstrumentRegistryService CreateService(
            IDbContextFactory<TradingBotDbContext> factory,
            TradingSettings settings,
            IClock clock)
        {
            return new InstrumentRegistryService(factory, Options.Create(settings), clock);
        }

        private static TradingSettings BuildSettings(params InstrumentSettings[] instruments)
        {
            return new TradingSettings
            {
                Instruments = instruments,
                MarketDataTimeframes = new[] { "1m" }
            };
        }

        private static InstrumentSettings Instrument(
            string instrumentId,
            string symbol,
            bool enabled,
            bool tradingEnabled)
        {
            return new InstrumentSettings
            {
                InstrumentId = instrumentId,
                Symbol = symbol,
                Exchange = "SMART",
                Currency = "USD",
                SecurityType = "STK",
                Enabled = enabled,
                TradingEnabled = tradingEnabled,
                AllowedDirections = new[] { TradeDirection.Long, TradeDirection.Short },
                StrategyIds = new[] { "deterministic-patterns" },
                MarketDataTimeframes = new[] { "1m" }
            };
        }

        private static SqliteConnection CreateDatabase(out IDbContextFactory<TradingBotDbContext> factory)
        {
            var connectionString = $"Data Source=instrument-registry-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var keeper = new SqliteConnection(connectionString);
            keeper.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>()
                .UseSqlite(connectionString)
                .Options;
            factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return keeper;
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBotDbContext CreateDbContext() => new(_options);

            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(CreateDbContext());
        }

        private sealed class MutableClock : IClock
        {
            public MutableClock(DateTime utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTime UtcNow { get; private set; }

            public void Advance(TimeSpan duration)
            {
                UtcNow = UtcNow.Add(duration);
            }
        }
    }
}
