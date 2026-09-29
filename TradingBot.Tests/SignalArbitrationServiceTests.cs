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
    public sealed class SignalArbitrationServiceTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 28, 16, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task ParallelSameStrategySignals_ProduceOnlyOneActiveAllocation()
        {
            using var harness = CreateHarness(new SignalArbitrationSettings());
            var results = await Task.WhenAll(
                harness.Service.ArbitrateAsync(Pattern("one", "strategy-a"), "DU123", Array.Empty<PositionDto>()),
                harness.Service.ArbitrateAsync(Pattern("two", "strategy-a"), "DU123", Array.Empty<PositionDto>()));

            Assert.Single(results, x => x.Approved);
            Assert.Single(results, x => !x.Approved);
            await using var db = harness.Factory.CreateDbContext();
            Assert.Single(await db.SignalArbitrationRecords.Where(x => x.Status == SignalArbitrationStatus.Accepted).ToListAsync());
        }

        [Fact]
        public async Task DifferentStrategies_CanHoldSameDirectionVirtualAllocations()
        {
            using var harness = CreateHarness(new SignalArbitrationSettings { MaximumActiveAllocationsPerInstrument = 3 });

            var first = await harness.Service.ArbitrateAsync(Pattern("one", "strategy-a"), "DU123", Array.Empty<PositionDto>());
            var second = await harness.Service.ArbitrateAsync(Pattern("two", "strategy-b"), "DU123", Array.Empty<PositionDto>());

            Assert.True(first.Approved);
            Assert.True(second.Approved);
        }

        [Fact]
        public async Task RejectPolicy_BlocksOpposingSignal()
        {
            using var harness = CreateHarness(new SignalArbitrationSettings { ConflictPolicy = SignalConflictPolicy.Reject });
            await harness.Service.ArbitrateAsync(Pattern("long", "strategy-a"), "DU123", Array.Empty<PositionDto>());

            var result = await harness.Service.ArbitrateAsync(Pattern("short", "strategy-b", TradeDirection.Short), "DU123", Array.Empty<PositionDto>());

            Assert.False(result.Approved);
            Assert.Contains("opposing", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PriorityPolicy_SupersedesOnlyUnsubmittedLowerPriorityIntent()
        {
            var settings = new SignalArbitrationSettings { ConflictPolicy = SignalConflictPolicy.Priority };
            settings.StrategyPriorities["low"] = 10;
            settings.StrategyPriorities["high"] = 100;
            using var harness = CreateHarness(settings);
            var low = await harness.Service.ArbitrateAsync(Pattern("low", "low"), "DU123", Array.Empty<PositionDto>());
            var reservationId = Guid.NewGuid();
            await harness.Service.BindReservationAsync(low.ArbitrationId!.Value, reservationId, 5m, 500m);

            var high = await harness.Service.ArbitrateAsync(Pattern("high", "high", TradeDirection.Short), "DU123", Array.Empty<PositionDto>());

            Assert.True(high.Approved);
            Assert.Contains(reservationId, harness.Portfolio.Released);
            await using var db = harness.Factory.CreateDbContext();
            Assert.Equal(SignalArbitrationStatus.Superseded, (await db.SignalArbitrationRecords.SingleAsync(x => x.Id == low.ArbitrationId)).Status);
        }

        [Fact]
        public async Task PriorityPolicy_CannotDisplaceIntentClaimedForBrokerSubmission()
        {
            var settings = new SignalArbitrationSettings { ConflictPolicy = SignalConflictPolicy.Priority };
            settings.StrategyPriorities["low"] = 10;
            settings.StrategyPriorities["high"] = 100;
            using var harness = CreateHarness(settings);
            var low = await harness.Service.ArbitrateAsync(Pattern("low", "low"), "DU123", Array.Empty<PositionDto>());
            await harness.Service.BindReservationAsync(low.ArbitrationId!.Value, Guid.NewGuid(), 5m, 500m);
            Assert.True((await harness.Service.ClaimForExecutionAsync(low.ArbitrationId.Value)).Approved);

            var high = await harness.Service.ArbitrateAsync(Pattern("high", "high", TradeDirection.Short), "DU123", Array.Empty<PositionDto>());

            Assert.False(high.Approved);
            Assert.Contains("already being submitted", high.Reason);
        }

        [Fact]
        public async Task VirtualAllocation_PreventsExitFromUsingAnotherStrategiesQuantity()
        {
            using var harness = CreateHarness(new SignalArbitrationSettings());
            var pattern = Pattern("allocation", "strategy-a");
            var arbitration = await harness.Service.ArbitrateAsync(pattern, "DU123", Array.Empty<PositionDto>());
            await harness.Service.BindReservationAsync(arbitration.ArbitrationId!.Value, Guid.NewGuid(), 10m, 1000m);
            await harness.Service.ClaimForExecutionAsync(arbitration.ArbitrationId.Value);
            await harness.Service.RecordSubmissionAsync(arbitration.ArbitrationId.Value, new ManagedOrderResult
            {
                Submitted = true,
                Status = OrderStatus.Submitted,
                BrokerOrderId = "ENTRY-1",
                ChildOrderIds = new[] { "EXIT-1" }
            });
            await harness.Service.RegisterExitOrderAsync(pattern.Context.SignalId, "EXIT-1", 10m);
            await harness.Execution.EmitFillAsync("ENTRY-1", 10m, 0m);

            Assert.True((await harness.Service.ValidateExitQuantityAsync(pattern.Context.SignalId, 10m)).Approved);
            Assert.False((await harness.Service.ValidateExitQuantityAsync(pattern.Context.SignalId, 11m)).Approved);

            await harness.Execution.EmitFillAsync("EXIT-1", 4m, 6m);
            Assert.True((await harness.Service.ValidateExitQuantityAsync(pattern.Context.SignalId, 6m)).Approved);
            Assert.False((await harness.Service.ValidateExitQuantityAsync(pattern.Context.SignalId, 7m)).Approved);
            await using var db = harness.Factory.CreateDbContext();
            Assert.Single(await db.VirtualAllocationOrderRecords.Where(x => x.BrokerOrderId == "EXIT-1").ToListAsync());
        }

        [Fact]
        public async Task BrokerNetPosition_BlocksOpposingDirection()
        {
            using var harness = CreateHarness(new SignalArbitrationSettings());
            var positions = new[] { new PositionDto { AccountId = "DU123", Symbol = "SPY", Quantity = 2m, AveragePrice = 100m } };

            var result = await harness.Service.ArbitrateAsync(Pattern("short", "strategy-a", TradeDirection.Short), "DU123", positions);

            Assert.False(result.Approved);
            Assert.Contains("existing long net position", result.Reason);
        }

        private static PatternCandidate Pattern(string key, string strategy, TradeDirection direction = TradeDirection.Long) => new(
            PatternType.BreakoutAndRetest,
            "SPY",
            Timeframe.OneMinute,
            NowUtc,
            0.8m,
            context: PipelineContext.CreateForSignal("US-STK-SPY-SMART", key, strategy),
            direction: direction);

        private static Harness CreateHarness(SignalArbitrationSettings settings)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new TestFactory(options);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            var portfolio = new FakePortfolioRiskService();
            var execution = new FakeExecutionService();
            var service = new SignalArbitrationService(factory, portfolio, execution, Options.Create(settings), new FixedClock(NowUtc), NullLogger<SignalArbitrationService>.Instance);
            return new Harness(connection, factory, portfolio, execution, service);
        }

        private sealed record Harness(SqliteConnection Connection, TestFactory Factory, FakePortfolioRiskService Portfolio, FakeExecutionService Execution, SignalArbitrationService Service) : IDisposable
        {
            public void Dispose()
            {
                Service.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class FakePortfolioRiskService : IPortfolioRiskService
        {
            public List<Guid> Released { get; } = new();
            public Task CommitAsync(Guid reservationId, string? brokerOrderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken = default) { Released.Add(reservationId); return Task.CompletedTask; }
            public Task<PortfolioRiskStateDto> GetStateAsync(string? accountId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<PortfolioReservationResult> TryReserveAsync(StrategyDecision strategyDecision, AccountInfo accountInfo, IReadOnlyList<PositionDto> currentPositions, IReadOnlyList<OrderStatusDto> openOrders, PositionSizingResult proposedSizing, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class FakeExecutionService : IOrderExecutionService
        {
            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;
            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async Task EmitFillAsync(string brokerOrderId, decimal filled, decimal remaining)
            {
                var status = new OrderStatusDto { OrderId = brokerOrderId, BrokerOrderId = brokerOrderId, Status = remaining > 0m ? "PartiallyFilled" : "Filled", FilledQuantity = filled, RemainingQuantity = remaining, TimestampUtc = NowUtc };
                if (OrderStatusUpdated != null) await OrderStatusUpdated(status);
                if (OrderFilled != null) await OrderFilled(status);
            }
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
