using System;
using System.Collections.Generic;
using System.Linq;
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
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class ExitManagementServiceTests
    {
        [Fact]
        public async Task InitialFillCreatesBrokerSideStopForFilledQuantity()
        {
            var harness = CreateHarness(TradingOperatingMode.PaperTrading);
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());

            await harness.Execution.EmitStatusAsync(EntryFill(35m, 65m, 100m));

            var stop = Assert.Single(harness.Execution.Submitted, x => x.Role == "ProtectiveStop");
            Assert.Equal("SELL", stop.Side);
            Assert.Equal("STOP", stop.Type);
            Assert.Equal(35m, stop.Quantity);
            Assert.Equal(99m, stop.StopPrice);

            await using var db = harness.Factory.CreateDbContext();
            var state = Assert.Single(await db.ExitManagementRecords.ToListAsync());
            Assert.Equal(ExitManagementState.InitialProtection, state.State);
            Assert.Equal(35m, state.ProtectedQuantity);
            Assert.Equal(99m, state.CurrentProtectiveStop);
            Assert.Equal("InitialProtection", Assert.Single(await db.ExitStopAuditRecords.ToListAsync()).Reason);
        }

        [Fact]
        public async Task BreakEvenActivatesAtConfiguredRThreshold()
        {
            var harness = CreateHarness(TradingOperatingMode.PaperTrading);
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());
            await harness.Execution.EmitStatusAsync(EntryFill(100m, 0m, 100m));

            await harness.Service.ProcessMarketCandleAsync(CandleAt(100.79m));
            Assert.Empty(harness.Execution.Modified);

            await harness.Service.ProcessMarketCandleAsync(CandleAt(100.80m, 100.80m));

            var modify = Assert.Single(harness.Execution.Modified);
            Assert.Equal(100.05m, modify.Request.StopPrice);
            await using var db = harness.Factory.CreateDbContext();
            var state = Assert.Single(await db.ExitManagementRecords.ToListAsync());
            Assert.True(state.BreakEvenActivated);
            Assert.Equal(ExitManagementState.BreakEvenProtection, state.State);
        }

        [Fact]
        public async Task TrailingActivatesAtThresholdAndNeverFalls()
        {
            var harness = CreateHarness(TradingOperatingMode.PaperTrading, atr: 0.50m);
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());
            await harness.Execution.EmitStatusAsync(EntryFill(100m, 0m, 100m));
            await SeedPriorCandleAsync(harness.Factory);

            await harness.Service.ProcessMarketCandleAsync(CandleAt(101.19m, 101.19m));
            Assert.DoesNotContain(harness.Execution.Modified, x => x.Request.StopPrice == 100.69m);

            await harness.Service.ProcessMarketCandleAsync(CandleAt(101.20m, 101.20m));
            Assert.Contains(harness.Execution.Modified, x => x.Request.StopPrice == 100.70m);

            harness.FeatureEngine.Atr = 2.00m;
            await harness.Service.ProcessMarketCandleAsync(CandleAt(101.00m, 101.00m, DateTime.UtcNow.AddMinutes(1)));

            var lastStop = harness.Execution.Modified.Last().Request.StopPrice;
            Assert.Equal(100.70m, lastStop);
        }

        [Fact]
        public async Task AdditionalPartialFillUpdatesProtectiveQuantityWithoutCreatingDuplicateStop()
        {
            var harness = CreateHarness(TradingOperatingMode.PaperTrading);
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());

            await harness.Execution.EmitStatusAsync(EntryFill(35m, 65m, 100m));
            await harness.Execution.EmitStatusAsync(EntryFill(75m, 25m, 100.10m));
            await harness.Execution.EmitStatusAsync(EntryFill(100m, 0m, 100.15m));

            Assert.Single(harness.Execution.Submitted, x => x.Role == "ProtectiveStop");
            Assert.Equal(new[] { 75m, 100m }, harness.Execution.Modified.Select(x => x.Request.Quantity));

            await using var db = harness.Factory.CreateDbContext();
            var state = Assert.Single(await db.ExitManagementRecords.ToListAsync());
            Assert.Equal(100m, state.FilledQuantity);
            Assert.Equal(100m, state.ProtectedQuantity);
        }

        [Fact]
        public async Task AnalysisOnlyNeverSubmitsProtectiveOrders()
        {
            var harness = CreateHarness(TradingOperatingMode.AnalysisOnly);
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());
            await harness.Execution.EmitStatusAsync(EntryFill(10m, 0m, 100m));

            Assert.Empty(harness.Execution.Submitted);
            Assert.Empty(harness.Execution.Modified);
        }

        [Fact]
        public async Task RejectedInitialStopSetsEngineDegraded()
        {
            var harness = CreateHarness(TradingOperatingMode.PaperTrading);
            harness.Execution.RejectStops = true;
            await harness.Service.RegisterApprovedEntryAsync(Plan(), EntryOrder());

            await harness.Execution.EmitStatusAsync(EntryFill(10m, 0m, 100m));

            Assert.Equal(TradingEngineState.Degraded, harness.Status.Current.State);
            await using var db = harness.Factory.CreateDbContext();
            var state = Assert.Single(await db.ExitManagementRecords.ToListAsync());
            Assert.Equal(ExitManagementState.Faulted, state.State);
        }

        private static Harness CreateHarness(TradingOperatingMode mode, decimal atr = 0.50m)
        {
            var execution = new FakeExecutionService();
            var root = new ServiceCollection();
            root.AddSingleton<IOrderModificationService>(execution);
            var serviceProvider = root.BuildServiceProvider();
            var factory = CreateInMemoryFactory(out var connection);
            var operatingMode = new OperatingModeService(
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = mode, LiveTradingExplicitlyEnabled = false }),
                factory,
                NullLogger<OperatingModeService>.Instance);
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready", brokerEnvironmentVerification: BrokerEnvironmentVerificationStatus.VerifiedPaper, reconciliationCompleted: true);
            var featureEngine = new FakeFeatureEngine { Atr = atr };
            var service = new ExitManagementService(
                execution,
                serviceProvider,
                factory,
                operatingMode,
                status,
                featureEngine,
                Options.Create(new ExitStrategySettings
                {
                    Mode = ExitStrategyMode.AtrTrailing,
                    BreakEvenTriggerR = 0.8m,
                    BreakEvenOffsetR = 0.05m,
                    TrailingActivationR = 1.2m,
                    TrailingAtrMultiplier = 1.0m,
                    TrailingAtrTimeframe = "1m",
                    MaximumHoldingMinutes = null
                }),
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = mode }),
                NullLogger<ExitManagementService>.Instance);

            return new Harness(service, execution, factory, connection, status, featureEngine, serviceProvider);
        }

        private static ApprovedTradePlan Plan()
        {
            return new ApprovedTradePlan
            {
                Pattern = new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, DateTime.UtcNow, 0.8m),
                StrategyDecision = new StrategyDecision
                {
                    Symbol = "SPY",
                    Approved = true,
                    EntryMin = 100m,
                    EntryMax = 100m,
                    StopPrice = 99m,
                    TakeProfitPrice = 103m
                },
                RiskDecision = new RiskDecision(RiskDecisionType.Approve, "ok", 100m, 10000m, 100m, DateTime.UtcNow),
                AiAnalysis = new AiMarketAnalysisResult { Action = AiMarketActions.Buy, Confidence = 0.8m },
                AiCriticAnalysis = new AiTradeCriticResult { Approved = true, Confidence = 0.8m }
            };
        }

        private static ManagedOrderResult EntryOrder()
        {
            return new ManagedOrderResult
            {
                OrderId = "ENTRY-1",
                BrokerOrderId = "ENTRY-1",
                Status = OrderStatus.Submitted,
                Submitted = true
            };
        }

        private static OrderStatusDto EntryFill(decimal filled, decimal remaining, decimal averagePrice)
        {
            return new OrderStatusDto
            {
                OrderId = "ENTRY-1",
                BrokerOrderId = "ENTRY-1",
                Status = remaining == 0m ? "Filled" : "PartiallyFilled",
                FilledQuantity = filled,
                RemainingQuantity = remaining,
                AverageFillPrice = averagePrice,
                LastFillPrice = averagePrice,
                TimestampUtc = DateTime.UtcNow
            };
        }

        private static Candle CandleAt(decimal close, decimal? high = null, DateTime? timestampUtc = null)
        {
            var timestamp = timestampUtc ?? DateTime.UtcNow;
            return new Candle("SPY", Timeframe.OneMinute, timestamp, close - 0.05m, high ?? close, close - 0.10m, close, 1000m);
        }

        private static async Task SeedPriorCandleAsync(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory)
        {
            await using var db = factory.CreateDbContext();
            db.Candles.Add(new TradingBot.Persistence.Candle
            {
                Symbol = "SPY",
                Timeframe = Timeframe.OneMinute,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1),
                Open = 100m,
                High = 100.1m,
                Low = 99.9m,
                Close = 100m,
                Volume = 1000m
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

        private sealed record Harness(
            ExitManagementService Service,
            FakeExecutionService Execution,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> Factory,
            SqliteConnection Connection,
            TradingEngineStatusService Status,
            FakeFeatureEngine FeatureEngine,
            ServiceProvider ServiceProvider) : IDisposable
        {
            public void Dispose()
            {
                Service.Dispose();
                ServiceProvider.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class FakeExecutionService : IOrderExecutionService, IOrderModificationService
        {
            private int _nextStopId = 1;

            public List<OrderRequestDto> Submitted { get; } = new();
            public List<(string BrokerOrderId, OrderRequestDto Request)> Modified { get; } = new();
            public bool RejectStops { get; set; }

            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;

            public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
            {
                Submitted.Add(request);
                var id = $"STOP-{_nextStopId++}";
                return Task.FromResult(new OrderStatusDto
                {
                    OrderId = id,
                    BrokerOrderId = id,
                    Status = RejectStops ? "Rejected" : "Submitted",
                    RemainingQuantity = request.Quantity,
                    TimestampUtc = DateTime.UtcNow,
                    Message = RejectStops ? "Rejected stop" : "Submitted"
                });
            }

            public Task<OrderStatusDto> ModifyOrderAsync(string brokerOrderId, OrderRequestDto request, CancellationToken cancellationToken = default)
            {
                Modified.Add((brokerOrderId, request));
                return Task.FromResult(new OrderStatusDto
                {
                    OrderId = brokerOrderId,
                    BrokerOrderId = brokerOrderId,
                    Status = "Submitted",
                    RemainingQuantity = request.Quantity,
                    TimestampUtc = DateTime.UtcNow
                });
            }

            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
            {
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

        private sealed class FakeFeatureEngine : IFeatureEngine
        {
            public decimal Atr { get; set; } = 0.50m;

            public MarketFeatures ComputeFeatures(IReadOnlyList<Candle> candles)
            {
                return new MarketFeatures
                {
                    Symbol = candles[^1].Symbol,
                    TimestampUtc = candles[^1].TimestampUtc,
                    Atr = Atr
                };
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
