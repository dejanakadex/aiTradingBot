using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class OrderManagerTests
    {
        [Fact]
        public async Task SubmitLimitBuyAsync_PersistsBrokerOrderIdAndDoesNotAssumeFilled()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);

            var result = await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            Assert.True(result.Submitted);
            Assert.Equal(OrderStatus.Submitted, result.Status);
            Assert.Equal("BRK-1", result.BrokerOrderId);
            Assert.Single(adapter.Submitted);

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal("BRK-1", order.BrokerOrderId);
                Assert.Equal(OrderStatus.Submitted, order.Status);
                Assert.Contains("averageFillPrice", order.RawJson);
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task SubmitLimitBuyAsync_PreventsDuplicateSubmission()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            var request = LimitBuy();

            var first = await manager.SubmitLimitBuyAsync(request, ApprovedRisk());
            var second = await manager.SubmitLimitBuyAsync(request, ApprovedRisk());

            Assert.False(second.Submitted);
            Assert.True(second.IsDuplicate);
            Assert.Equal(first.BrokerOrderId, second.BrokerOrderId);
            Assert.Single(adapter.Submitted);

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task SubmitLimitBuyAsync_PreventsConcurrentDuplicateSubmission()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            adapter.SubmitDelay = TimeSpan.FromMilliseconds(50);
            var request = LimitBuy();

            var results = await Task.WhenAll(
                manager.SubmitLimitBuyAsync(request, ApprovedRisk()),
                manager.SubmitLimitBuyAsync(request, ApprovedRisk()));

            Assert.Single(results, r => r.Submitted);
            Assert.Single(results, r => r.IsDuplicate);
            Assert.Single(adapter.Submitted);

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task SubmitBracketOrderAsync_SubmitsEntryStopAndTarget()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);

            var result = await manager.SubmitBracketOrderAsync(
                LimitBuy(),
                new OrderRequest("SPY", OrderSide.Sell, OrderType.Stop, 10m, null, DateTime.UtcNow),
                new OrderRequest("SPY", OrderSide.Sell, OrderType.Limit, 10m, 105m, DateTime.UtcNow),
                ApprovedRisk());

            Assert.True(result.Submitted);
            Assert.Equal(3, adapter.Submitted.Count);
            Assert.Equal(new[] { "LIMIT", "STOP", "LIMIT" }, adapter.Submitted.Select(o => o.Type));
            Assert.Equal(2, result.ChildOrderIds.Count);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(3, await db.OrderRecords.CountAsync());
                Assert.Contains(await db.OrderRecords.ToListAsync(), o => o.RawJson.Contains("ProtectiveStop"));
                Assert.Contains(await db.OrderRecords.ToListAsync(), o => o.RawJson.Contains("TakeProfit"));
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task BrokerFillUpdate_TracksPartialAndFinalFillsWithExecutionRecords()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            await adapter.EmitStatusAsync(new OrderStatusDto
            {
                OrderId = "BRK-1",
                BrokerOrderId = "BRK-1",
                Status = "PartiallyFilled",
                FilledQuantity = 4m,
                RemainingQuantity = 6m,
                AverageFillPrice = 100.25m,
                TimestampUtc = DateTime.UtcNow
            });

            await adapter.EmitFillAsync(new OrderStatusDto
            {
                OrderId = "BRK-1",
                BrokerOrderId = "BRK-1",
                Status = "Filled",
                FilledQuantity = 10m,
                RemainingQuantity = 0m,
                AverageFillPrice = 100.40m,
                LastFillPrice = 100.50m,
                Commission = 1.25m,
                BrokerExecutionId = "EXEC-1",
                TimestampUtc = DateTime.UtcNow
            });

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.Filled, order.Status);
                Assert.Contains("100.40", order.RawJson);
                Assert.Contains("1.25", order.RawJson);

                var execution = Assert.Single(await db.ExecutionRecords.ToListAsync());
                Assert.Equal("EXEC-1", execution.BrokerExecutionId);
                Assert.Equal(ExecutionStatus.Completed, execution.Status);
                Assert.Contains("100.50", execution.RawJson);
                Assert.Contains("1.25", execution.RawJson);
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task CancelOrderAsync_PersistsCanceledStatus()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            var cancelled = await manager.CancelOrderAsync("BRK-1");

            Assert.True(cancelled);
            Assert.Contains("BRK-1", adapter.Cancelled);

            var status = await manager.GetOrderStatusAsync("BRK-1");
            Assert.NotNull(status);
            Assert.Equal(OrderStatus.Cancelled, status!.Status);

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task RejectedOrder_IsPersistedAsRejected()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            adapter.NextStatus = "Rejected";

            var result = await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            Assert.False(result.Submitted);
            Assert.Equal(OrderStatus.Rejected, result.Status);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Equal(OrderStatus.Rejected, Assert.Single(await db.OrderRecords.ToListAsync()).Status);
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task AdapterDisconnectDuringSubmission_IsSafeRejectedAndPersisted()
        {
            var manager = CreateManager(out var adapter, out var factory, out var connection);
            adapter.ThrowOnSubmit = true;

            var result = await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            Assert.False(result.Submitted);
            Assert.Equal(OrderStatus.Rejected, result.Status);
            Assert.Contains("Execution adapter unavailable", result.Message);

            await using (var db = factory.CreateDbContext())
            {
                var order = Assert.Single(await db.OrderRecords.ToListAsync());
                Assert.Equal(OrderStatus.Rejected, order.Status);
                Assert.Contains("Execution adapter unavailable", order.RawJson);
            }

            connection.Dispose();
            manager.Dispose();
        }

        [Fact]
        public async Task SubmitLimitBuyAsync_WhenGuardRejects_DoesNotCallExecutionAdapter()
        {
            var manager = CreateManager(new RejectingExecutionGuard("guard rejected"), out var adapter, out _, out var connection);

            var result = await manager.SubmitLimitBuyAsync(LimitBuy(), ApprovedRisk());

            Assert.False(result.Submitted);
            Assert.Equal(OrderStatus.Rejected, result.Status);
            Assert.Contains("guard rejected", result.Message);
            Assert.Empty(adapter.Submitted);

            connection.Dispose();
            manager.Dispose();
        }

        private static OrderManager CreateManager(out FakeOrderExecutionService adapter, out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, out SqliteConnection connection)
        {
            return CreateManager(new AllowingExecutionGuard(), out adapter, out factory, out connection);
        }

        private static OrderManager CreateManager(ITradingExecutionGuard guard, out FakeOrderExecutionService adapter, out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, out SqliteConnection connection)
        {
            adapter = new FakeOrderExecutionService();
            factory = CreateInMemoryFactory(out connection);
            return new OrderManager(adapter, guard, factory, NullLogger<OrderManager>.Instance);
        }

        private static OrderRequest LimitBuy()
        {
            return new OrderRequest("SPY", OrderSide.Buy, OrderType.Limit, 10m, 100m, new DateTime(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc));
        }

        private static RiskDecision ApprovedRisk()
        {
            return new RiskDecision(RiskDecisionType.Approve, "ok", 10m, 1000m, 50m, DateTime.UtcNow);
        }

        private sealed class AllowingExecutionGuard : ITradingExecutionGuard
        {
            public Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(RiskDecision? riskDecision, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(TradingExecutionGuardResult.Approve());
            }
        }

        private sealed class RejectingExecutionGuard : ITradingExecutionGuard
        {
            private readonly string _reason;

            public RejectingExecutionGuard(string reason)
            {
                _reason = reason;
            }

            public Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(RiskDecision? riskDecision, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(TradingExecutionGuardResult.Reject(new[] { _reason }));
            }
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

        private sealed class FakeOrderExecutionService : IOrderExecutionService
        {
            private int _nextOrderId = 1;

            public List<OrderRequestDto> Submitted { get; } = new();
            public List<string> Cancelled { get; } = new();
            public string NextStatus { get; set; } = "Submitted";
            public bool ThrowOnSubmit { get; set; }
            public TimeSpan SubmitDelay { get; set; } = TimeSpan.Zero;

            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;

            public async Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
            {
                if (ThrowOnSubmit) throw new InvalidOperationException("IBKR disconnected");
                if (SubmitDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SubmitDelay, cancellationToken);
                }

                Submitted.Add(request);
                var brokerOrderId = $"BRK-{_nextOrderId++}";
                return new OrderStatusDto
                {
                    OrderId = brokerOrderId,
                    BrokerOrderId = brokerOrderId,
                    Status = NextStatus,
                    RemainingQuantity = request.Quantity,
                    TimestampUtc = DateTime.UtcNow
                };
            }

            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
            {
                Cancelled.Add(orderId);
                return Task.FromResult(true);
            }

            public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IEnumerable<OrderStatusDto>>(Array.Empty<OrderStatusDto>());
            }

            public Task EmitStatusAsync(OrderStatusDto status)
            {
                return OrderStatusUpdated?.Invoke(status) ?? Task.CompletedTask;
            }

            public Task EmitFillAsync(OrderStatusDto status)
            {
                return OrderFilled?.Invoke(status) ?? Task.CompletedTask;
            }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBot.Persistence.TradingBotDbContext CreateDbContext()
            {
                return new TradingBot.Persistence.TradingBotDbContext(_options);
            }

            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBot.Persistence.TradingBotDbContext>(CreateDbContext());
            }
        }
    }
}
