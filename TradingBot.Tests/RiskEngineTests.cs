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
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class RiskEngineTests
    {
        private static readonly DateTime NowUtc = new(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void PositionSizer_UsesRiskDistanceAsPrimaryLimit()
        {
            var sizer = CreateSizer(new RiskSettings
            {
                MaximumRiskPerTrade = 100m,
                MaximumPositionValue = 100000m,
                MaximumLeverage = 10m
            });

            var result = sizer.CalculatePositionSize(100m, 95m, BuildAccount(), Array.Empty<PositionDto>());

            Assert.Equal(20m, result.Quantity);
            Assert.Equal(2000m, result.PositionValue);
            Assert.Equal(100m, result.RiskAmount);
            Assert.Empty(result.RejectionReasons);
        }

        [Fact]
        public void PositionSizer_EnforcesMaximumPositionValue()
        {
            var sizer = CreateSizer(new RiskSettings
            {
                MaximumRiskPerTrade = 1000m,
                MaximumPositionValue = 500m,
                MaximumLeverage = 10m
            });

            var result = sizer.CalculatePositionSize(100m, 95m, BuildAccount(), Array.Empty<PositionDto>());

            Assert.Equal(5m, result.Quantity);
            Assert.Equal(500m, result.PositionValue);
        }

        [Fact]
        public void PositionSizer_EnforcesBuyingPower()
        {
            var sizer = CreateSizer(new RiskSettings
            {
                MaximumRiskPerTrade = 1000m,
                MaximumPositionValue = 100000m,
                MaximumLeverage = 10m
            });

            var result = sizer.CalculatePositionSize(100m, 95m, BuildAccount(buyingPower: 250m), Array.Empty<PositionDto>());

            Assert.Equal(2.5m, result.Quantity);
            Assert.Equal(250m, result.PositionValue);
        }

        [Fact]
        public void PositionSizer_EnforcesLeverageRemaining()
        {
            var sizer = CreateSizer(new RiskSettings
            {
                MaximumRiskPerTrade = 1000m,
                MaximumPositionValue = 100000m,
                MaximumLeverage = 1m
            });

            var positions = new[] { new PositionDto { Symbol = "QQQ", Quantity = 90m, AveragePrice = 100m } };
            var result = sizer.CalculatePositionSize(100m, 95m, BuildAccount(netLiquidation: 10000m, buyingPower: 100000m), positions);

            Assert.Equal(10m, result.Quantity);
            Assert.Equal(1000m, result.PositionValue);
        }

        [Fact]
        public async Task RiskEngine_ApprovesAtExactLimitsAndPersists()
        {
            var engine = CreateEngine(out var factory, out var connection, new RiskSettings
            {
                MaximumRiskPerTrade = 100m,
                MaximumPositionValue = 2000m,
                MaximumDailyLoss = 100m,
                MaximumLeverage = 10m,
                MaximumOpenPositions = 1,
                MaximumConsecutiveLosses = 1
            });

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), Array.Empty<PositionDto>(), Array.Empty<Trade>(), Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Approve, decision.Decision);
            Assert.Equal(20m, decision.ApprovedQuantity);
            Assert.Equal(2000m, decision.PositionValue);
            Assert.Equal(100m, decision.RiskAmount);
            Assert.NotNull(decision.Context);
            await AssertPersisted(factory, "riskDecision", "\"approvedQuantity\":20");
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsUnapprovedStrategyDecision()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings());
            var strategy = BuildStrategyDecision(approved: false, includeSignal: false);

            var decision = await engine.EvaluateAsync(strategy, BuildAccount(), Array.Empty<PositionDto>(), Array.Empty<Trade>(), Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Strategy decision is not approved"));
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Trade signal is unavailable"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsDailyLossAtLimit()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings(maximumDailyLoss: 100m));
            var losses = new[] { ClosedTrade(entry: 100m, exit: 90m, quantity: 10m) };

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), Array.Empty<PositionDto>(), losses, Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Maximum daily loss"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsOpenPositionLimitIncludingOpenOrders()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings(maximumOpenPositions: 2));
            var positions = new[] { new PositionDto { Symbol = "AAPL", Quantity = 1m, AveragePrice = 100m } };
            var openOrders = new[] { new OrderStatusDto { OrderId = "1", Status = "Submitted", TimestampUtc = DateTime.UtcNow } };

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), positions, Array.Empty<Trade>(), openOrders);

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Maximum open position"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsConsecutiveLossLimit()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings(maximumConsecutiveLosses: 2));
            var trades = new[]
            {
                ClosedTrade(entry: 100m, exit: 99m, quantity: 1m, minutesAgo: 1),
                ClosedTrade(entry: 100m, exit: 98m, quantity: 1m, minutesAgo: 2)
            };

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), Array.Empty<PositionDto>(), trades, Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("consecutive losses"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsMaximumTradesPerDay()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings(maximumTradesPerDay: 2));
            var trades = new[]
            {
                ClosedTrade(entry: 100m, exit: 101m, quantity: 1m, minutesAgo: 30),
                ClosedTrade(entry: 100m, exit: 101m, quantity: 1m, minutesAgo: 10)
            };

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), Array.Empty<PositionDto>(), trades, Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Maximum trades per day"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsLossCooldownAfterRecentConsecutiveLosses()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings(
                maximumConsecutiveLosses: 10,
                lossCooldownAfterConsecutiveLosses: 2,
                lossCooldownDurationMinutes: 30));
            var trades = new[]
            {
                ClosedTrade(entry: 100m, exit: 99m, quantity: 1m, minutesAgo: 1),
                ClosedTrade(entry: 100m, exit: 98m, quantity: 1m, minutesAgo: 2)
            };

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), Array.Empty<PositionDto>(), trades, Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Loss cooldown"));
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_RejectsInvalidStopDistance()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings());
            var strategy = BuildStrategyDecision(entry: 100m, stop: 101m);

            var decision = await engine.EvaluateAsync(strategy, BuildAccount(), Array.Empty<PositionDto>(), Array.Empty<Trade>(), Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains("Stop price must be below entry price", decision.Reason);
            connection.Dispose();
        }

        [Fact]
        public async Task RiskEngine_UnavailableCollectionsRejectSafelyAndPersist()
        {
            var engine = CreateEngine(out var factory, out var connection, BuildSettings());

            var decision = await engine.EvaluateAsync(BuildStrategyDecision(), BuildAccount(), null!, Array.Empty<Trade>(), Array.Empty<OrderStatusDto>());

            Assert.Equal(RiskDecisionType.Reject, decision.Decision);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Current broker positions are unavailable"));
            await AssertPersisted(factory, "Current broker positions are unavailable");
            connection.Dispose();
        }

        private static PositionSizer CreateSizer(RiskSettings settings)
        {
            return new PositionSizer(Options.Create(settings));
        }

        private static RiskEngine CreateEngine(out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, out SqliteConnection connection, RiskSettings settings)
        {
            factory = CreateInMemoryFactory(out connection);
            var sizer = new PositionSizer(Options.Create(settings));
            return new RiskEngine(Options.Create(settings), sizer, factory, new FixedClock(NowUtc), NullLogger<RiskEngine>.Instance);
        }

        private static RiskSettings BuildSettings(
            decimal maximumDailyLoss = 1000m,
            int maximumOpenPositions = 3,
            int maximumConsecutiveLosses = 3,
            int maximumTradesPerDay = 5,
            int lossCooldownAfterConsecutiveLosses = 3,
            int lossCooldownDurationMinutes = 30)
        {
            return new RiskSettings
            {
                MaximumRiskPerTrade = 100m,
                MaximumPositionValue = 2000m,
                MaximumDailyLoss = maximumDailyLoss,
                MaximumLeverage = 10m,
                MaximumOpenPositions = maximumOpenPositions,
                MaximumConsecutiveLosses = maximumConsecutiveLosses,
                MaximumTradesPerDay = maximumTradesPerDay,
                LossCooldownAfterConsecutiveLosses = lossCooldownAfterConsecutiveLosses,
                LossCooldownDurationMinutes = lossCooldownDurationMinutes
            };
        }

        private static StrategyDecision BuildStrategyDecision(bool approved = true, bool includeSignal = true, decimal entry = 100m, decimal stop = 95m)
        {
            var signal = includeSignal
                ? new TradeSignal("SPY", Timeframe.OneMinute, TradeAction.Enter, 0.8m, 1m, NowUtc)
                : null;

            return new StrategyDecision
            {
                Symbol = "SPY",
                Approved = approved,
                TradeSignal = signal,
                EntryMin = entry,
                EntryMax = entry,
                StopPrice = stop,
                TakeProfitPrice = 110m,
                ExpectedRewardRiskRatio = 2m,
                Context = PipelineContext.CreateForSignal("US-STK-SPY-SMART", $"risk-test|{entry}|{stop}")
            };
        }

        private static AccountInfo BuildAccount(decimal netLiquidation = 10000m, decimal buyingPower = 10000m)
        {
            return new AccountInfo
            {
                AccountId = "DU123",
                NetLiquidation = netLiquidation,
                AvailableFunds = buyingPower,
                BuyingPower = buyingPower,
                MarginRequirement = 0m
            };
        }

        private static Trade ClosedTrade(decimal entry, decimal exit, decimal quantity, int minutesAgo = 1)
        {
            var trade = new Trade("SPY", quantity, entry, NowUtc.AddMinutes(-minutesAgo - 1));
            trade.Close(exit, NowUtc.AddMinutes(-minutesAgo));
            return trade;
        }

        private static async Task AssertPersisted(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, params string[] expectedFragments)
        {
            await using var db = factory.CreateDbContext();
            var record = Assert.Single(await db.RiskDecisionRecords.ToListAsync());
            foreach (var fragment in expectedFragments)
            {
                Assert.Contains(fragment, record.DecisionJson);
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

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTime UtcNow { get; }
        }
    }
}
