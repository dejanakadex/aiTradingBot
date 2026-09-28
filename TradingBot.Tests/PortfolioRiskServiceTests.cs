using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class PortfolioRiskServiceTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 28, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task ParallelReservations_CannotConsumeTheSameSingleSlot()
        {
            using var harness = CreateHarness(new RiskSettings
            {
                MaximumOpenPositions = 10,
                MaximumPendingReservations = 1,
                MaximumPendingReservationsPerInstrument = 10,
                MaximumPendingReservationsPerStrategy = 10,
                MaximumGrossExposure = 10000m,
                MaximumNetExposure = 10000m,
                MaximumInstrumentExposure = 10000m,
                MaximumStrategyExposure = 10000m,
                MaximumCorrelationGroupExposure = 10000m,
                MaximumLeverage = 10m,
                InstrumentCooldownSeconds = 0
            });

            var tasks = Enumerable.Range(0, 2).Select(index => harness.Service.TryReserveAsync(
                BuildStrategy($"signal-{index}"),
                BuildAccount(),
                Array.Empty<PositionDto>(),
                Array.Empty<OrderStatusDto>(),
                BuildSizing(),
                CancellationToken.None));
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, x => x.Approved);
            Assert.Single(results, x => !x.Approved);
            await using var db = harness.Factory.CreateDbContext();
            Assert.Single(await db.PortfolioRiskReservationRecords.Where(x => x.Status == PortfolioReservationStatus.Pending).ToListAsync());
            Assert.Single(await db.PortfolioRiskReservationAuditRecords.ToListAsync());
        }

        [Fact]
        public async Task Reservation_IsDurableAndTransitionsThroughCommitAndRelease()
        {
            using var harness = CreateHarness(BuildSettings());
            var result = await harness.Service.TryReserveAsync(
                BuildStrategy("durable"), BuildAccount(), Array.Empty<PositionDto>(), Array.Empty<OrderStatusDto>(), BuildSizing());

            Assert.True(result.Approved);
            Assert.NotNull(result.ReservationId);
            await harness.Service.CommitAsync(result.ReservationId!.Value, "broker-42");
            await harness.Service.ReleaseAsync(result.ReservationId.Value, "filled position is broker-visible");

            await using var db = harness.Factory.CreateDbContext();
            var stored = await db.PortfolioRiskReservationRecords.SingleAsync();
            Assert.Equal(PortfolioReservationStatus.Released, stored.Status);
            Assert.Equal("broker-42", stored.BrokerOrderId);
            Assert.Equal(3, await db.PortfolioRiskReservationAuditRecords.CountAsync());
        }

        [Fact]
        public async Task CorrelationLimit_ReducesQuantityToRemainingCapacity()
        {
            var settings = BuildSettings();
            settings.MaximumCorrelationGroupExposure = 1000m;
            settings.CorrelationGroups["broad-index"] = new[] { "SPY", "QQQ" };
            using var harness = CreateHarness(settings);
            var positions = new[] { new PositionDto { AccountId = "DU123", Symbol = "QQQ", Quantity = 8m, AveragePrice = 100m } };

            var result = await harness.Service.TryReserveAsync(
                BuildStrategy("correlated"), BuildAccount(), positions, Array.Empty<OrderStatusDto>(), BuildSizing());

            Assert.True(result.Approved);
            Assert.Equal(200m, result.PositionValue);
            Assert.Equal(2m, result.Quantity);
        }

        private static RiskSettings BuildSettings() => new()
        {
            MaximumOpenPositions = 10,
            MaximumPendingReservations = 10,
            MaximumPendingReservationsPerInstrument = 10,
            MaximumPendingReservationsPerStrategy = 10,
            MaximumGrossExposure = 10000m,
            MaximumNetExposure = 10000m,
            MaximumInstrumentExposure = 10000m,
            MaximumStrategyExposure = 10000m,
            MaximumCorrelationGroupExposure = 10000m,
            MaximumLeverage = 10m,
            InstrumentCooldownSeconds = 0,
            ReservationTimeoutSeconds = 120,
            CommittedReservationTimeoutSeconds = 300
        };

        private static StrategyDecision BuildStrategy(string signalKey) => new()
        {
            Symbol = "SPY",
            Approved = true,
            EntryMin = 100m,
            StopPrice = 95m,
            Context = PipelineContext.CreateForSignal("US-STK-SPY-SMART", signalKey)
        };

        private static AccountInfo BuildAccount() => new()
        {
            AccountId = "DU123",
            NetLiquidation = 10000m,
            BuyingPower = 10000m,
            AvailableFunds = 10000m
        };

        private static PositionSizingResult BuildSizing() => new() { Quantity = 6m, PositionValue = 600m, RiskAmount = 30m };

        private static Harness CreateHarness(RiskSettings settings)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new TestFactory(options);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            var service = new PortfolioRiskService(factory, Options.Create(settings), new FixedClock(NowUtc), NullLogger<PortfolioRiskService>.Instance);
            return new Harness(connection, factory, service);
        }

        private sealed record Harness(SqliteConnection Connection, TestFactory Factory, PortfolioRiskService Service) : IDisposable
        {
            public void Dispose() => Connection.Dispose();
        }

        private sealed class TestFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;
            public TestFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options) => _options = options;
            public TradingBot.Persistence.TradingBotDbContext CreateDbContext() => new(_options);
            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
