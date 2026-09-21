using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
#pragma warning disable CS0067
    public class BrokerStateReconciliationTests
    {
        [Fact]
        public async Task ReconcileAsync_WhenBrokerAndSqliteMatch_MarksReady()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await SeedOpenTradeAsync(factory, "SPY", 25m);
            await SeedOpenOrderAsync(factory, "BRK-1", "SPY");

            var status = new TradingEngineStatusService();
            var service = CreateService(
                factory,
                status,
                brokerPositions: new[] { new PositionDto { Symbol = "SPY", Quantity = 25m, AveragePrice = 100m } },
                brokerOrders: new[] { new OrderStatusDto { OrderId = "BRK-1", BrokerOrderId = "BRK-1", Status = "Submitted", TimestampUtc = DateTime.UtcNow } });

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Ready, result.State);
            Assert.True(result.TradingEnabled);
            Assert.Empty(result.Mismatches);
            Assert.Equal(TradingEngineState.Ready, status.Current.State);
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_WhenBrokerHasPositionMissingInSqlite_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = CreateService(
                factory,
                status,
                brokerPositions: new[] { new PositionDto { Symbol = "SPY", Quantity = 25m, AveragePrice = 100m } },
                brokerOrders: Array.Empty<OrderStatusDto>());

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Contains(result.Mismatches, m => m.Contains("Position mismatch for SPY") && m.Contains("SQLite=0") && m.Contains("IBKR=25"));
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_WhenSqliteOpenOrderMissingInBroker_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await SeedOpenOrderAsync(factory, "BRK-99", "SPY");

            var status = new TradingEngineStatusService();
            var service = CreateService(factory, status, Array.Empty<PositionDto>(), Array.Empty<OrderStatusDto>());

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Contains(result.Mismatches, m => m.Contains("SQLite contains broker order BRK-99"));
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_WhenBrokerOpenOrderMissingInSqlite_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = CreateService(
                factory,
                status,
                Array.Empty<PositionDto>(),
                new[] { new OrderStatusDto { OrderId = "BRK-22", BrokerOrderId = "BRK-22", Status = "Submitted", TimestampUtc = DateTime.UtcNow } });

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Contains(result.Mismatches, m => m.Contains("IBKR contains broker order BRK-22"));
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_WhenConnectionDoesNotConnect_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = CreateService(factory, status, Array.Empty<PositionDto>(), Array.Empty<OrderStatusDto>(), ConnectionStatus.Failed);

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Contains(result.Mismatches, m => m.Contains("Connection status"));
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_WhenBrokerStateReadFails_MarksFaultedAndDisablesTrading()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = CreateService(
                factory,
                status,
                Array.Empty<PositionDto>(),
                Array.Empty<OrderStatusDto>(),
                throwPositionRead: true);

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Faulted, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Contains(result.Mismatches, m => m.Contains("Broker position read failed"));
            connection.Dispose();
        }

        private static BrokerStateReconciliationService CreateService(
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory,
            ITradingEngineStatusService status,
            IReadOnlyList<PositionDto> brokerPositions,
            IReadOnlyList<OrderStatusDto> brokerOrders,
            ConnectionStatus connectionStatus = ConnectionStatus.Connected,
            bool throwPositionRead = false)
        {
            return new BrokerStateReconciliationService(
                new FakeIbkrConnectionService(connectionStatus),
                new FakeAccountService("DU123"),
                new FakePositionService(brokerPositions, throwPositionRead),
                new FakeOrderExecutionService(brokerOrders),
                factory,
                status,
                Options.Create(new IbkrSettings { AccountId = "DU123" }),
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.AnalysisOnly }),
                NullLogger<BrokerStateReconciliationService>.Instance);
        }

        [Fact]
        public async Task ReconcileAsync_PaperTradingWithLiveAccount_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = new BrokerStateReconciliationService(
                new FakeIbkrConnectionService(ConnectionStatus.Connected),
                new FakeAccountService("LIVE123"),
                new FakePositionService(Array.Empty<PositionDto>(), false),
                new FakeOrderExecutionService(Array.Empty<OrderStatusDto>()),
                factory,
                status,
                Options.Create(new IbkrSettings { AccountId = "LIVE123", PaperAccountId = "PAPER123" }),
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading }),
                NullLogger<BrokerStateReconciliationService>.Instance);

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Equal(BrokerEnvironmentVerificationStatus.Mismatch, result.BrokerEnvironmentVerification);
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_LiveTradingWithoutExplicitGate_RemainsDegraded()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = new BrokerStateReconciliationService(
                new FakeIbkrConnectionService(ConnectionStatus.Connected),
                new FakeAccountService("LIVE123"),
                new FakePositionService(Array.Empty<PositionDto>(), false),
                new FakeOrderExecutionService(Array.Empty<OrderStatusDto>()),
                factory,
                status,
                Options.Create(new IbkrSettings { AccountId = "LIVE123", PaperAccountId = "PAPER123" }),
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.LiveTrading, LiveTradingExplicitlyEnabled = false }),
                NullLogger<BrokerStateReconciliationService>.Instance);

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Degraded, result.State);
            Assert.False(result.TradingEnabled);
            Assert.Equal(BrokerEnvironmentVerificationStatus.Unknown, result.BrokerEnvironmentVerification);
            connection.Dispose();
        }

        [Fact]
        public async Task ReconcileAsync_AnalysisOnlyWithPaperAccountConfigured_VerifiesPaperAccount()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            var service = new BrokerStateReconciliationService(
                new FakeIbkrConnectionService(ConnectionStatus.Connected),
                new FakeAccountService("PAPER123"),
                new FakePositionService(Array.Empty<PositionDto>(), false),
                new FakeOrderExecutionService(Array.Empty<OrderStatusDto>()),
                factory,
                status,
                Options.Create(new IbkrSettings { AccountId = string.Empty, PaperAccountId = "PAPER123" }),
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.AnalysisOnly }),
                NullLogger<BrokerStateReconciliationService>.Instance);

            var result = await service.ReconcileAsync();

            Assert.Equal(TradingEngineState.Ready, result.State);
            Assert.Equal(BrokerEnvironmentVerificationStatus.VerifiedPaper, result.BrokerEnvironmentVerification);
            connection.Dispose();
        }

        private static async Task SeedOpenTradeAsync(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, string symbol, decimal size)
        {
            await using var db = factory.CreateDbContext();
            db.Trades.Add(new TradingBot.Persistence.Trade
            {
                Symbol = symbol,
                Size = size,
                EntryPrice = 100m,
                OpenedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        private static async Task SeedOpenOrderAsync(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, string brokerOrderId, string symbol)
        {
            await using var db = factory.CreateDbContext();
            db.OrderRecords.Add(new TradingBot.Persistence.OrderRecord
            {
                BrokerOrderId = brokerOrderId,
                Symbol = symbol,
                CreatedUtc = DateTime.UtcNow,
                Status = OrderStatus.Submitted,
                RawJson = "{}"
            });
            await db.SaveChangesAsync();
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

        private sealed class FakeIbkrConnectionService : IIbkrConnectionService
        {
            private readonly ConnectionStatus _statusAfterConnect;

            public FakeIbkrConnectionService(ConnectionStatus statusAfterConnect)
            {
                _statusAfterConnect = statusAfterConnect;
            }

            public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

            public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                Status = _statusAfterConnect;
                return ConnectionStatusChanged?.Invoke(Status) ?? Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                Status = ConnectionStatus.Disconnected;
                return Task.CompletedTask;
            }
        }

        private sealed class FakePositionService : IPositionService
        {
            private readonly IReadOnlyList<PositionDto> _positions;
            private readonly bool _throwOnRead;

            public FakePositionService(IReadOnlyList<PositionDto> positions, bool throwOnRead)
            {
                _positions = positions;
                _throwOnRead = throwOnRead;
            }

            public event Func<PositionDto, Task>? PositionUpdated;

            public Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default)
            {
                if (_throwOnRead) throw new InvalidOperationException("Broker position read failed");
                return Task.FromResult<IEnumerable<PositionDto>>(_positions);
            }
        }

        private sealed class FakeAccountService : IAccountService
        {
            private readonly string _accountId;

            public FakeAccountService(string accountId)
            {
                _accountId = accountId;
            }

            public event Func<AccountInfo, Task>? AccountUpdated;

            public Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AccountInfo
                {
                    AccountId = _accountId,
                    NetLiquidation = 100000m,
                    BuyingPower = 100000m
                });
            }
        }

        private sealed class FakeOrderExecutionService : IOrderExecutionService
        {
            private readonly IReadOnlyList<OrderStatusDto> _orders;

            public FakeOrderExecutionService(IReadOnlyList<OrderStatusDto> orders)
            {
                _orders = orders;
            }

            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;

            public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IEnumerable<OrderStatusDto>>(_orders);
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
#pragma warning restore CS0067
}
