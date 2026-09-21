using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class PostTradeAnalysisServiceTests
    {
        [Fact]
        public async Task StoreCompletedTradeAnalysisAsync_PersistsReconstructionContextAndCalculatesTradeMetrics()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var service = new PostTradeAnalysisService(factory);
            var opened = new DateTime(2026, 8, 24, 14, 30, 0, DateTimeKind.Utc);
            var closed = opened.AddMinutes(15);

            var input = new PostTradeAnalysisInput
            {
                SourceTradeId = 42,
                Symbol = "SPY",
                OpenedUtc = opened,
                ClosedUtc = closed,
                Quantity = 10m,
                EntryPrice = 100m,
                ExitPrice = 104m,
                CommissionsFees = 1.25m,
                MarketSnapshot = new MarketSnapshot
                {
                    Symbol = "SPY",
                    CreatedAtUtc = opened,
                    CurrentPrice = 100m,
                    OneMinute = new MarketTimeframeSnapshot
                    {
                        Timeframe = Timeframe.OneMinute,
                        Features = new MarketFeatures { Symbol = "SPY", TimestampUtc = opened, Vwap = 99m, DistanceFromVwapPercent = 1m }
                    },
                    FifteenMinutes = new MarketTimeframeSnapshot
                    {
                        Timeframe = Timeframe.FifteenMinutes,
                        Trend = new TrendInformation { Label = "Bullish", Direction = 1 }
                    }
                },
                PatternCandidate = new PatternCandidate(PatternType.DoubleBottom, "SPY", Timeframe.OneMinute, opened, 0.82m, new[] { 99m, 104m }),
                AiMarketAnalysis = new AiMarketAnalysisResult
                {
                    Action = AiMarketActions.Buy,
                    Confidence = 0.81m,
                    PatternQuality = 0.77m,
                    MarketRegime = "pullback in uptrend",
                    ExpectedMovePercent = 4m,
                    ExpectedHorizonMinutes = 20,
                    EntryMin = 100m,
                    EntryMax = 101m,
                    InvalidationPrice = 98m,
                    Reason = "fake e2e setup"
                },
                AiCriticAnalysis = new AiTradeCriticResult
                {
                    Approved = true,
                    Confidence = 0.75m,
                    RiskFlags = Array.Empty<string>(),
                    Reason = "no rejection"
                },
                StrategyDecision = new StrategyDecision
                {
                    Symbol = "SPY",
                    Approved = true,
                    EntryMin = 100m,
                    EntryMax = 101m,
                    StopPrice = 98m,
                    TakeProfitPrice = 106m,
                    ExpectedRewardRiskRatio = 2m
                },
                RiskDecision = new RiskDecision(RiskDecisionType.Approve, "ok", 10m, 1000m, 20m, opened),
                Orders = new[]
                {
                    new ManagedOrderResult { BrokerOrderId = "entry-1", Status = OrderStatus.Filled, Submitted = true, AverageFillPrice = 100m }
                },
                Executions = new[]
                {
                    new PostTradeExecutionData { ExecutionId = "exec-1", OrderId = "entry-1", ExecutedAtUtc = opened, Quantity = 10m, Price = 100m, Commission = 1.25m, Status = "Filled" }
                },
                CandlesDuringTrade = new[]
                {
                    new Candle("SPY", Timeframe.OneMinute, opened.AddMinutes(1), 100m, 103m, 99m, 102m, 1000m),
                    new Candle("SPY", Timeframe.OneMinute, opened.AddMinutes(2), 102m, 105m, 98.5m, 104m, 1200m)
                }
            };

            var summary = await service.StoreCompletedTradeAnalysisAsync(input);
            var filteredStats = await service.GetPatternPerformanceAsync("DoubleBottom", new PatternAnalysisFilter
            {
                Pattern = "DoubleBottom",
                Symbol = "SPY",
                AboveVwap = true,
                FifteenMinuteTrend = "Bullish"
            });

            Assert.Equal(40m, summary.GrossProfitLoss);
            Assert.Equal(1.25m, summary.CommissionsFees);
            Assert.Equal(38.75m, summary.NetProfitLoss);
            Assert.Equal(50m, summary.MaximumFavorableExcursion);
            Assert.Equal(15m, summary.MaximumAdverseExcursion);
            Assert.Equal(15m, summary.HoldingDurationMinutes);
            Assert.Equal(1, filteredStats.Trades);
            Assert.Equal(1, filteredStats.Wins);

            await using (var db = factory.CreateDbContext())
            {
                var record = await db.PostTradeAnalysisRecords.SingleAsync();
                Assert.Equal("Win", record.TradeResult);
                Assert.True(record.AboveVwap);
                Assert.Contains("SPY", record.MarketSnapshotJson);
                Assert.Contains("exec-1", record.ExecutionDataJson);
                Assert.Contains("entry-1", record.OrderDataJson);
            }

            connection.Dispose();
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

            public TradingBot.Persistence.TradingBotDbContext CreateDbContext() => new(_options);

            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBot.Persistence.TradingBotDbContext>(CreateDbContext());
            }
        }
    }
}
