using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using TradingBot.Tests.Fakes;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class FakeIbkrOrderLifecycleTests
    {
        [Fact]
        public async Task PartialFill_TracksWeightedAveragePositionExecutionsAndOrderState()
        {
            var broker = new DeterministicFakeIbkrBroker
            {
                Scenario = new FakeBrokerScenario
                {
                    OrderBehavior = FakeBrokerOrderBehavior.PartialFill,
                    FillQuantities = new[] { 35m, 40m, 25m },
                    FillPrices = new[] { 100m, 101m, 102m },
                    CommissionPerFill = 0.25m
                }
            };
            var manager = CreateManager(broker, out var factory, out var connection);

            await broker.ConnectAsync();
            var result = await manager.SubmitLimitBuyAsync(LimitBuy(100m), ApprovedRisk(100m));
            await broker.ReplayExecutionCallbacksAsync();

            Assert.True(result.Submitted);
            Assert.Equal(OrderStatus.Filled, result.Status);
            Assert.Equal(100m, result.FilledQuantity);
            Assert.Equal(0m, result.RemainingQuantity);
            Assert.Equal(100.9m, result.AverageFillPrice);

            var position = Assert.Single(await broker.GetPositionsAsync("DU123"));
            Assert.Equal(100m, position.Quantity);
            Assert.Equal(100.9m, position.AveragePrice);

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.Filled, order.Status);
                Assert.False(string.IsNullOrWhiteSpace(order.ClientOrderKey));
                Assert.Equal(3, await db.ExecutionRecords.CountAsync());
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task PartialFillThenCancel_PreservesFilledPositionAndCancelsRemaining()
        {
            var broker = new DeterministicFakeIbkrBroker
            {
                Scenario = new FakeBrokerScenario
                {
                    OrderBehavior = FakeBrokerOrderBehavior.PartialFill,
                    FillQuantities = new[] { 40m },
                    FillPrices = new[] { 100m }
                }
            };
            var manager = CreateManager(broker, out _, out var connection);

            await broker.ConnectAsync();
            var result = await manager.SubmitLimitBuyAsync(LimitBuy(100m), ApprovedRisk(100m));
            var cancelled = await manager.CancelOrderAsync(result.BrokerOrderId!);

            Assert.True(cancelled);
            var position = Assert.Single(await broker.GetPositionsAsync("DU123"));
            Assert.Equal(40m, position.Quantity);

            var openOrders = await broker.GetOpenOrdersAsync();
            Assert.Empty(openOrders);

            connection.Dispose();
            manager.Dispose();
        }

        [Theory]
        [InlineData(FakeBrokerOrderBehavior.RejectOrder, "Fake broker rejected order")]
        [InlineData(FakeBrokerOrderBehavior.RejectForInsufficientBuyingPower, "Insufficient buying power")]
        [InlineData(FakeBrokerOrderBehavior.RejectForInvalidPrice, "Invalid price")]
        [InlineData(FakeBrokerOrderBehavior.RejectBecauseMarketClosed, "Market is closed")]
        public async Task BrokerRejection_PersistsRejectedOrderAndCreatesNoPosition(FakeBrokerOrderBehavior behavior, string reason)
        {
            var broker = new DeterministicFakeIbkrBroker
            {
                Scenario = new FakeBrokerScenario { OrderBehavior = behavior }
            };
            var manager = CreateManager(broker, out var factory, out var connection);

            await broker.ConnectAsync();
            var result = await manager.SubmitLimitBuyAsync(LimitBuy(10m), ApprovedRisk(10m));

            Assert.False(result.Submitted);
            Assert.Equal(OrderStatus.Rejected, result.Status);
            Assert.Contains(reason, result.Message);
            Assert.Empty(await broker.GetPositionsAsync("DU123"));

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.Rejected, order.Status);
                Assert.Contains(reason, order.RawJson);
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task DisconnectAfterBrokerAcceptedOrder_PersistsPendingConfirmationAndRetryDoesNotCreateDuplicateBuy()
        {
            var broker = new DeterministicFakeIbkrBroker
            {
                Scenario = new FakeBrokerScenario { OrderBehavior = FakeBrokerOrderBehavior.DisconnectAfterBrokerAcceptedOrder }
            };
            var manager = CreateManager(broker, out var factory, out var connection);
            var request = LimitBuy(10m);

            await broker.ConnectAsync();
            var first = await manager.SubmitLimitBuyAsync(request, ApprovedRisk(10m));

            broker.Scenario = new FakeBrokerScenario { OrderBehavior = FakeBrokerOrderBehavior.AcceptImmediately };
            await broker.ForceReconnectAsync();
            var second = await manager.SubmitLimitBuyAsync(request, ApprovedRisk(10m));

            Assert.False(first.Submitted);
            Assert.Equal(OrderStatus.PendingBrokerConfirmation, first.Status);
            Assert.False(second.Submitted);
            Assert.True(second.IsDuplicate);
            Assert.Single(broker.SubmittedRequests);

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.PendingBrokerConfirmation, order.Status);
                Assert.False(string.IsNullOrWhiteSpace(order.ClientOrderKey));
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task DisconnectAfterBrokerAcceptedOrder_RestartedOrderManagerStillBlocksDuplicateBuy()
        {
            var broker = new DeterministicFakeIbkrBroker
            {
                Scenario = new FakeBrokerScenario { OrderBehavior = FakeBrokerOrderBehavior.DisconnectAfterBrokerAcceptedOrder }
            };
            var manager = CreateManager(broker, out var factory, out var connection);
            var request = LimitBuy(10m);

            await broker.ConnectAsync();
            var first = await manager.SubmitLimitBuyAsync(request, ApprovedRisk(10m));
            manager.Dispose();

            broker.Scenario = new FakeBrokerScenario { OrderBehavior = FakeBrokerOrderBehavior.AcceptImmediately };
            await broker.ForceReconnectAsync();
            using var restartedManager = new OrderManager(broker, new AllowingExecutionGuard(), factory, NullLogger<OrderManager>.Instance);
            var second = await restartedManager.SubmitLimitBuyAsync(request, ApprovedRisk(10m));

            Assert.False(first.Submitted);
            Assert.Equal(OrderStatus.PendingBrokerConfirmation, first.Status);
            Assert.False(second.Submitted);
            Assert.True(second.IsDuplicate);
            Assert.Single(broker.SubmittedRequests);

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.PendingBrokerConfirmation, order.Status);
            }

            connection.Dispose();
        }

        private static OrderManager CreateManager(DeterministicFakeIbkrBroker broker, out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, out SqliteConnection connection)
        {
            factory = CreateInMemoryFactory(out connection);
            return new OrderManager(broker, new AllowingExecutionGuard(), factory, NullLogger<OrderManager>.Instance);
        }

        private static OrderRequest LimitBuy(decimal quantity)
        {
            return new OrderRequest("SPY", OrderSide.Buy, OrderType.Limit, quantity, 100m, new DateTime(2026, 8, 24, 14, 30, 0, DateTimeKind.Utc));
        }

        private static RiskDecision ApprovedRisk(decimal quantity)
        {
            return new RiskDecision(RiskDecisionType.Approve, "ok", quantity, quantity * 100m, quantity * 5m, DateTime.UtcNow);
        }

        private static IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection connection)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>()
                .UseSqlite(connection)
                .Options;

            var factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return factory;
        }

        private sealed class AllowingExecutionGuard : ITradingExecutionGuard
        {
            public Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(RiskDecision? riskDecision, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(TradingExecutionGuardResult.Approve());
            }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBot.Persistence.TradingBotDbContext CreateDbContext() => new(_options);

            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBot.Persistence.TradingBotDbContext>(CreateDbContext());
            }
        }
    }
}
