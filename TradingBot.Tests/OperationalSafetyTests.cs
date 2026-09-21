using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class OperationalSafetyTests
    {
        private static readonly DateTime NowUtc = new(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void MarketDataValidator_RejectsCorruptedFutureAndStaleBars()
        {
            var validator = new MarketDataValidator(
                Options.Create(new TradingSettings { MaximumCandleAgeSeconds = 300 }),
                new FixedClock(NowUtc));

            Assert.False(validator.TryValidate(Bar(high: 99m, low: 100m), out var highLowReason));
            Assert.Contains("High is below low", highLowReason);

            Assert.False(validator.TryValidate(Bar(open: 120m), out var rangeReason));
            Assert.Contains("outside high/low", rangeReason);

            Assert.False(validator.TryValidate(Bar(volume: -1m), out var volumeReason));
            Assert.Contains("Volume cannot be negative", volumeReason);

            Assert.False(validator.TryValidate(Bar(timestamp: NowUtc.AddSeconds(10)), out var futureReason));
            Assert.Contains("future", futureReason);

            Assert.False(validator.TryValidate(Bar(timestamp: NowUtc.AddMinutes(-10)), out var staleReason));
            Assert.Contains("stale", staleReason);
        }

        [Fact]
        public async Task AiUsageLimiter_RejectsWhenMinuteLimitReached()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await using (var db = factory.CreateDbContext())
            {
                db.AiApiUsageRecords.Add(new TradingBot.Persistence.AiApiUsageRecord
                {
                    RequestType = "MarketAnalyzer",
                    Symbol = "SPY",
                    TimestampUtc = NowUtc.AddSeconds(-10),
                    Success = true
                });
                await db.SaveChangesAsync();
            }

            var limiter = new AiUsageLimiter(
                factory,
                Options.Create(new OpenAiSettings
                {
                    MaximumAiCallsPerMinute = 1,
                    MaximumAiCallsPerDay = 100,
                    MaximumAiInputTokensPerDay = 10000,
                    MaximumAiOutputTokensPerDay = 10000
                }),
                new FixedClock(NowUtc),
                NullLogger<AiUsageLimiter>.Instance);

            var decision = await limiter.CheckAsync();

            Assert.False(decision.Approved);
            Assert.Contains("last minute", decision.Reason);
            connection.Dispose();
        }

        [Fact]
        public async Task AiUsageLimiter_GlobalDailyLimitCountsAnalyzerAndCriticTogether()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await using (var db = factory.CreateDbContext())
            {
                db.AiApiUsageRecords.AddRange(
                    new TradingBot.Persistence.AiApiUsageRecord
                    {
                        AgentType = "Analyzer",
                        RequestType = "MarketAnalyzer",
                        Symbol = "SPY",
                        TimestampUtc = NowUtc.AddHours(-1),
                        InputTokens = 100,
                        OutputTokens = 20,
                        Model = "model-A",
                        PromptVersion = "analyzer-v1",
                        Success = true
                    },
                    new TradingBot.Persistence.AiApiUsageRecord
                    {
                        AgentType = "Critic",
                        RequestType = "TradeCritic",
                        Symbol = "SPY",
                        TimestampUtc = NowUtc.AddMinutes(-30),
                        InputTokens = 80,
                        OutputTokens = 10,
                        Model = "model-B",
                        PromptVersion = "critic-v1",
                        Success = true
                    });
                await db.SaveChangesAsync();
            }

            var limiter = new AiUsageLimiter(
                factory,
                Options.Create(new OpenAiSettings
                {
                    MaximumAiCallsPerMinute = 10,
                    MaximumAiCallsPerDay = 2,
                    MaximumAiInputTokensPerDay = 10000,
                    MaximumAiOutputTokensPerDay = 10000
                }),
                new FixedClock(NowUtc),
                NullLogger<AiUsageLimiter>.Instance);

            var decision = await limiter.CheckAsync();

            Assert.False(decision.Approved);
            Assert.Contains("Daily AI calls", decision.Reason);
            connection.Dispose();
        }

        [Fact]
        public async Task ProtectiveStopMonitor_DegradesWhenOpenPositionHasNoBrokerSideStop()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await using (var db = factory.CreateDbContext())
            {
                db.Trades.Add(new TradingBot.Persistence.Trade
                {
                    Symbol = "SPY",
                    OpenedUtc = NowUtc,
                    Size = 10m,
                    EntryPrice = 100m
                });
                await db.SaveChangesAsync();
            }

            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);
            var monitor = new ProtectiveStopMonitor(
                factory,
                new FakeOrderExecutionService(Array.Empty<OrderStatusDto>()),
                status,
                Options.Create(new TradingSettings { RequireProtectiveStopForBotPositions = true }),
                NullLogger<ProtectiveStopMonitor>.Instance);

            var ok = await monitor.ValidateAsync();

            Assert.False(ok);
            Assert.Equal(TradingEngineState.Degraded, status.Current.State);
            Assert.Contains(status.Current.Mismatches, m => m.Contains("no verified broker-side protective stop"));
            connection.Dispose();
        }

        [Fact]
        public async Task ProtectiveStopMonitor_AllowsVerifiedBrokerSideStop()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await using (var db = factory.CreateDbContext())
            {
                db.Trades.Add(new TradingBot.Persistence.Trade
                {
                    Symbol = "SPY",
                    OpenedUtc = NowUtc,
                    Size = 10m,
                    EntryPrice = 100m
                });
                db.OrderRecords.Add(new TradingBot.Persistence.OrderRecord
                {
                    BrokerOrderId = "stop-1",
                    ClientOrderKey = "test-stop",
                    Symbol = "SPY",
                    CreatedUtc = NowUtc,
                    Status = OrderStatus.Submitted,
                    RawJson = "{\"role\":\"ProtectiveStop\",\"type\":\"STOP\"}"
                });
                await db.SaveChangesAsync();
            }

            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);
            var monitor = new ProtectiveStopMonitor(
                factory,
                new FakeOrderExecutionService(new[]
                {
                    new OrderStatusDto { OrderId = "stop-1", BrokerOrderId = "stop-1", Status = "Submitted", TimestampUtc = NowUtc }
                }),
                status,
                Options.Create(new TradingSettings { RequireProtectiveStopForBotPositions = true }),
                NullLogger<ProtectiveStopMonitor>.Instance);

            var ok = await monitor.ValidateAsync();

            Assert.True(ok);
            Assert.Equal(TradingEngineState.Ready, status.Current.State);
            connection.Dispose();
        }

        [Fact]
        public async Task RuntimeBrokerReconciliation_RunsAfterConnectedEvent()
        {
            var services = new ServiceCollection();
            var connection = new FakeIbkrConnectionService();
            var status = new TradingEngineStatusService();
            var reconciliation = new FakeBrokerReconciliationService();
            services.AddSingleton<IIbkrConnectionService>(connection);
            services.AddSingleton<ITradingEngineStatusService>(status);
            services.AddSingleton<IBrokerStateReconciliationService>(reconciliation);
            await using var provider = services.BuildServiceProvider();

            var hosted = new RuntimeBrokerReconciliationHostedService(
                provider,
                status,
                NullLogger<RuntimeBrokerReconciliationHostedService>.Instance);

            await hosted.StartAsync(CancellationToken.None);
            status.SetState(TradingEngineState.Degraded, false, "Runtime reconciliation required.");
            await connection.RaiseAsync(ConnectionStatus.Connected);
            await hosted.StopAsync(CancellationToken.None);

            Assert.Equal(1, reconciliation.Calls);
        }

        [Fact]
        public async Task RuntimeBrokerReconciliation_IgnoresConnectedEventDuringStartupReconciliation()
        {
            var services = new ServiceCollection();
            var connection = new FakeIbkrConnectionService();
            var status = new TradingEngineStatusService();
            var reconciliation = new FakeBrokerReconciliationService();
            services.AddSingleton<IIbkrConnectionService>(connection);
            services.AddSingleton<ITradingEngineStatusService>(status);
            services.AddSingleton<IBrokerStateReconciliationService>(reconciliation);
            await using var provider = services.BuildServiceProvider();

            var hosted = new RuntimeBrokerReconciliationHostedService(
                provider,
                status,
                NullLogger<RuntimeBrokerReconciliationHostedService>.Instance);

            await hosted.StartAsync(CancellationToken.None);
            status.SetState(TradingEngineState.Reconciling, false, "Startup reconciliation in progress.");
            await connection.RaiseAsync(ConnectionStatus.Connected);
            await hosted.StopAsync(CancellationToken.None);

            Assert.Equal(0, reconciliation.Calls);
        }

        [Fact]
        public async Task MigrationHostedService_AppliesWalAndBusyTimeoutForSqlite()
        {
            var databasePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tradingbot-{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={databasePath};Pooling=False;Default Timeout=5";
            try
            {
                {
                    var services = new ServiceCollection();
                    services.AddDbContextFactory<TradingBot.Persistence.TradingBotDbContext>(options =>
                        options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(5)));
                    await using var provider = services.BuildServiceProvider();

                    var migration = new TradingBot.Persistence.MigrationHostedService(
                        provider,
                        NullLogger<TradingBot.Persistence.MigrationHostedService>.Instance);

                    await migration.StartAsync(CancellationToken.None);

                    await using var connection = new SqliteConnection(connectionString);
                    await connection.OpenAsync();
                    await using var journalCommand = connection.CreateCommand();
                    journalCommand.CommandText = "PRAGMA journal_mode;";
                    var journalMode = (string?)await journalCommand.ExecuteScalarAsync();
                    await using var busyCommand = connection.CreateCommand();
                    busyCommand.CommandText = "SELECT 1;";
                    busyCommand.CommandTimeout = connection.DefaultTimeout;

                    Assert.Equal("wal", journalMode, ignoreCase: true);
                    Assert.Equal(5, connection.DefaultTimeout);
                    Assert.Equal(5, busyCommand.CommandTimeout);
                }
            }
            finally
            {
                foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                    }
                }
            }
        }

        private static MarketBar Bar(
            DateTime? timestamp = null,
            decimal open = 100m,
            decimal high = 101m,
            decimal low = 99m,
            decimal close = 100.5m,
            decimal volume = 1000m)
        {
            return new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = timestamp ?? NowUtc,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            };
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

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow)
            {
                UtcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            }

            public DateTime UtcNow { get; }
        }

        private sealed class FakeOrderExecutionService : IOrderExecutionService
        {
            private readonly IEnumerable<OrderStatusDto> _openOrders;

            public FakeOrderExecutionService(IEnumerable<OrderStatusDto> openOrders)
            {
                _openOrders = openOrders;
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
                _ = OrderStatusUpdated;
                _ = OrderFilled;
                return Task.FromResult(_openOrders);
            }
        }

        private sealed class FakeIbkrConnectionService : IIbkrConnectionService
        {
            public ConnectionStatus Status { get; private set; }

            public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                Status = ConnectionStatus.Connected;
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                Status = ConnectionStatus.Disconnected;
                return Task.CompletedTask;
            }

            public async Task RaiseAsync(ConnectionStatus status)
            {
                Status = status;
                if (ConnectionStatusChanged != null)
                {
                    await ConnectionStatusChanged(status);
                }
            }
        }

        private sealed class FakeBrokerReconciliationService : IBrokerStateReconciliationService
        {
            public int Calls { get; private set; }

            public Task<TradingBot.Domain.Models.BrokerReconciliationStatus> ReconcileAsync(CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(new TradingBot.Domain.Models.BrokerReconciliationStatus
                {
                    State = TradingEngineState.Ready,
                    TradingEnabled = true,
                    ReconciliationCompleted = true
                });
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
