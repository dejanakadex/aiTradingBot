using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class BackgroundPipelineTests
    {
        [Fact]
        public async Task TradePipelineChannel_PublishesApprovedTradePlans()
        {
            var channel = new TradePipelineChannel();
            var plan = BuildPlan();

            await channel.PublishAsync(plan);

            var read = await channel.ApprovedTradePlanReader.ReadAsync(new CancellationTokenSource(1000).Token);
            Assert.Equal("SPY", read.StrategyDecision.Symbol);
        }

        [Fact]
        public async Task ApprovedOrderExecutionBackgroundService_DoesNotSubmitUnlessReadyAndTradingEnabled()
        {
            var channel = new TradePipelineChannel();
            var orderManager = new FakeOrderManager();
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Degraded, false, "not ready");
            var mode = CreateModeService(out var connection, out var factory, TradingOperatingMode.PaperTrading);

            var service = new ApprovedOrderExecutionBackgroundService(
                channel,
                orderManager,
                status,
                mode,
                factory,
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading }),
                NullLogger<ApprovedOrderExecutionBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await channel.PublishAsync(BuildPlan());
            await Task.Delay(100);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, orderManager.SubmitCount);
            connection.Dispose();
        }

        [Fact]
        public async Task ApprovedOrderExecutionBackgroundService_SubmitsBracketWhenReadyAndTradingEnabled()
        {
            var channel = new TradePipelineChannel();
            var orderManager = new FakeOrderManager();
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready");
            var mode = CreateModeService(out var connection, out var factory, TradingOperatingMode.PaperTrading);

            var service = new ApprovedOrderExecutionBackgroundService(
                channel,
                orderManager,
                status,
                mode,
                factory,
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading }),
                NullLogger<ApprovedOrderExecutionBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await channel.PublishAsync(BuildPlan());

            using var timeout = new CancellationTokenSource(2000);
            while (orderManager.SubmitCount == 0 && !timeout.IsCancellationRequested)
            {
                await Task.Delay(25, timeout.Token);
            }

            await service.StopAsync(CancellationToken.None);

            Assert.Equal(1, orderManager.SubmitCount);
            Assert.Equal(OrderType.Limit, orderManager.Entry!.Type);
            Assert.Equal(OrderType.Stop, orderManager.Stop!.Type);
            Assert.Equal(95m, orderManager.Stop.Price);
            Assert.Equal(OrderType.Limit, orderManager.Target!.Type);
            connection.Dispose();
        }

        [Fact]
        public async Task ApprovedOrderExecutionBackgroundService_DoesNotSubmitWhenApplicationTradingDisabled()
        {
            var channel = new TradePipelineChannel();
            var orderManager = new FakeOrderManager();
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready");
            var mode = CreateModeService(out var connection, out var factory, TradingOperatingMode.PaperTrading);

            var service = new ApprovedOrderExecutionBackgroundService(
                channel,
                orderManager,
                status,
                mode,
                factory,
                Options.Create(new TradingSettings { Enabled = false, OperatingMode = TradingOperatingMode.PaperTrading }),
                NullLogger<ApprovedOrderExecutionBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await channel.PublishAsync(BuildPlan());
            await Task.Delay(100);
            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, orderManager.SubmitCount);
            connection.Dispose();
        }

        [Fact]
        public async Task ApprovedOrderExecutionBackgroundService_AnalysisOnlyNeverSubmitsBrokerOrder()
        {
            var channel = new TradePipelineChannel();
            var orderManager = new FakeOrderManager();
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready");
            var mode = CreateModeService(out var connection, out var factory, TradingOperatingMode.AnalysisOnly);

            var service = new ApprovedOrderExecutionBackgroundService(
                channel,
                orderManager,
                status,
                mode,
                factory,
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.AnalysisOnly }),
                NullLogger<ApprovedOrderExecutionBackgroundService>.Instance);

            await service.StartAsync(CancellationToken.None);
            await channel.PublishAsync(BuildPlan());

            using var timeout = new CancellationTokenSource(2000);
            while (!timeout.IsCancellationRequested)
            {
                await using var pollDb = factory.CreateDbContext();
                if (await pollDb.StrategyDecisionRecords.AnyAsync(timeout.Token))
                {
                    break;
                }

                await Task.Delay(25, timeout.Token);
            }

            await service.StopAsync(CancellationToken.None);

            Assert.Equal(0, orderManager.SubmitCount);
            await using (var db = factory.CreateDbContext())
            {
                var record = Assert.Single(await db.StrategyDecisionRecords.ToListAsync());
                Assert.Contains("HypotheticalTrade", record.DecisionJson);
                Assert.Contains("AnalysisOnly", record.DecisionJson);
            }

            connection.Dispose();
        }

        private static ApprovedTradePlan BuildPlan()
        {
            var signal = new TradeSignal("SPY", Timeframe.OneMinute, TradeAction.Enter, 0.8m, 1m, DateTime.UtcNow);
            return new ApprovedTradePlan
            {
                StrategyDecision = new StrategyDecision
                {
                    Symbol = "SPY",
                    Approved = true,
                    TradeSignal = signal,
                    EntryMin = 100m,
                    EntryMax = 101m,
                    StopPrice = 95m,
                    TakeProfitPrice = 110m
                },
                Pattern = new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, DateTime.UtcNow, 0.8m),
                RiskDecision = new RiskDecision(RiskDecisionType.Approve, "ok", 10m, 1000m, 50m, DateTime.UtcNow)
            };
        }

        private static OperatingModeService CreateModeService(out SqliteConnection connection, out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, TradingOperatingMode mode)
        {
            factory = CreateInMemoryFactory(out connection);
            return new OperatingModeService(
                Options.Create(new TradingSettings { Enabled = true, OperatingMode = mode }),
                factory,
                NullLogger<OperatingModeService>.Instance);
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

        private sealed class FakeOrderManager : IOrderManager
        {
            public int SubmitCount { get; private set; }
            public OrderRequest? Entry { get; private set; }
            public OrderRequest? Stop { get; private set; }
            public OrderRequest? Target { get; private set; }

            public Task<ManagedOrderResult> SubmitLimitBuyAsync(OrderRequest orderRequest, RiskDecision riskDecision, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ManagedOrderResult> SubmitBracketOrderAsync(OrderRequest entryLimitBuy, OrderRequest stopLoss, OrderRequest takeProfit, RiskDecision riskDecision, CancellationToken cancellationToken = default)
            {
                SubmitCount++;
                Entry = entryLimitBuy;
                Stop = stopLoss;
                Target = takeProfit;
                return Task.FromResult(new ManagedOrderResult
                {
                    BrokerOrderId = "BRK-1",
                    Status = OrderStatus.Submitted,
                    Submitted = true
                });
            }

            public Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public Task<ManagedOrderResult?> GetOrderStatusAsync(string brokerOrderId, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
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
