using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class DashboardAnalyticsServiceTests
    {
        [Fact]
        public async Task TradeDashboardService_ReturnsProjectedTradeRows()
        {
            var factory = CreateInMemoryFactory(out var connection);
            await using (var db = factory.CreateDbContext())
            {
                db.Trades.Add(new TradingBot.Persistence.Trade
                {
                    Symbol = "SPY",
                    OpenedUtc = DateTime.UtcNow,
                    ClosedUtc = DateTime.UtcNow,
                    EntryPrice = 100m,
                    ExitPrice = 105m,
                    Size = 2m,
                    Notes = """{"pattern":"Hammer","aiConfidence":0.75,"fees":1.5}"""
                });
                await db.SaveChangesAsync();
            }

            var service = new TradeDashboardService(factory);
            var rows = await service.GetTradesAsync();

            var row = Assert.Single(rows);
            Assert.Equal("Hammer", row.Pattern);
            Assert.Equal(0.75m, row.AiConfidence);
            Assert.Equal(10m, row.GrossProfitLoss);
            Assert.Equal(1.5m, row.Fees);
            Assert.Equal(8.5m, row.NetProfitLoss);
            Assert.Equal("Win", row.TradeResult);

            connection.Dispose();
        }

        [Fact]
        public async Task PatternAnalysisDashboardService_CalculatesAggregateStatisticsAndFilters()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var now = DateTime.UtcNow;
            await using (var db = factory.CreateDbContext())
            {
                db.PatternDetections.AddRange(
                    new TradingBot.Persistence.PatternDetection { Symbol = "SPY", PatternType = PatternType.Hammer, DetectedAtUtc = now.AddMinutes(-10), Details = """{"aboveVwap":true,"fifteenMinuteTrend":"Up"}""" },
                    new TradingBot.Persistence.PatternDetection { Symbol = "SPY", PatternType = PatternType.Hammer, DetectedAtUtc = now.AddMinutes(-5), Details = """{"aboveVwap":true,"fifteenMinuteTrend":"Up"}""" },
                    new TradingBot.Persistence.PatternDetection { Symbol = "QQQ", PatternType = PatternType.DoubleBottom, DetectedAtUtc = now.AddMinutes(-3), Details = """{"aboveVwap":false,"fifteenMinuteTrend":"Down"}""" });

                db.Trades.AddRange(
                    new TradingBot.Persistence.Trade { Symbol = "SPY", OpenedUtc = now.AddMinutes(-9), ClosedUtc = now, EntryPrice = 100m, ExitPrice = 105m, Size = 1m, Notes = """{"pattern":"Hammer","marketRegime":"pullback","aboveVwap":true,"fifteenMinuteTrend":"Up","aiDecision":"BUY","fees":1}""" },
                    new TradingBot.Persistence.Trade { Symbol = "SPY", OpenedUtc = now.AddMinutes(-4), ClosedUtc = now, EntryPrice = 100m, ExitPrice = 98m, Size = 1m, Notes = """{"pattern":"Hammer","marketRegime":"pullback","aboveVwap":true,"fifteenMinuteTrend":"Up","aiDecision":"BUY","fees":1}""" });

                db.AiAnalysisRecords.AddRange(
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = now.AddMinutes(-9.5),
                        AgentType = AiAgentType.Pipeline.ToString(),
                        Context = "TradeSetupCandidateSentToAi",
                        ResultJson = """{"recordType":"TradeSetupCandidateSentToAi","reason":"TradeSetupCandidate passed deterministic pattern quality gate."}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = now.AddMinutes(-9),
                        AgentType = AiAgentType.Analyzer.ToString(),
                        Context = """{"detectedPatternSymbol":"SPY","detectedPatternName":"Hammer"}""",
                        ResultJson = """{"result":{"action":"BUY","confidence":0.8,"marketRegime":"pullback"}}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = now.AddMinutes(-8.8),
                        AgentType = AiAgentType.Critic.ToString(),
                        Context = """{"detectedPatternSymbol":"SPY","detectedPatternName":"Hammer"}""",
                        ResultJson = """{"result":{"approved":true,"confidence":0.7}}"""
                    });
                db.StrategyDecisionRecords.Add(new TradingBot.Persistence.StrategyDecisionRecord { TimestampUtc = now.AddMinutes(-9), DecisionJson = """{"approved":true}""" });
                db.RiskDecisionRecords.Add(new TradingBot.Persistence.RiskDecisionRecord { TimestampUtc = now.AddMinutes(-9), DecisionJson = """{"decision":"Approve"}""" });
                await db.SaveChangesAsync();
            }

            var service = new PatternAnalysisDashboardService(factory);
            var dashboard = await service.GetAnalysisAsync(new PatternAnalysisFilter
            {
                Pattern = "Hammer",
                Symbol = "SPY",
                MarketRegime = "pullback",
                AboveVwap = true,
                FifteenMinuteTrend = "Up",
                AiDecision = "BUY"
            });

            var stats = Assert.Single(dashboard.Statistics);
            Assert.Equal("Hammer", stats.Pattern);
            Assert.Equal(2, stats.Detections);
            Assert.Equal(2, stats.Trades);
            Assert.Equal(1, stats.Wins);
            Assert.Equal(1, stats.Losses);
            Assert.Equal(0.5m, stats.WinRate);
            Assert.Equal(1.33m, Math.Round(stats.ProfitFactor!.Value, 2));
            Assert.All(dashboard.Rows, row => Assert.Equal("Hammer", row.PatternCandidate));
            Assert.All(dashboard.Rows, row => Assert.Equal("BUY", row.AiDecision));
            Assert.All(dashboard.Rows, row => Assert.Equal(true.ToString(), row.CriticDecision));

            connection.Dispose();
        }

        [Fact]
        public async Task PatternAnalysisDashboardService_DoesNotAttachUnrelatedAiRecordByTimestamp()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var now = DateTime.UtcNow;
            await using (var db = factory.CreateDbContext())
            {
                db.PatternDetections.Add(new TradingBot.Persistence.PatternDetection
                {
                    Symbol = "SPY",
                    PatternType = PatternType.Hammer,
                    DetectedAtUtc = now.AddMinutes(-2),
                    Details = """{"aboveVwap":true,"fifteenMinuteTrend":"Up"}"""
                });

                db.AiAnalysisRecords.Add(new TradingBot.Persistence.AiAnalysisRecord
                {
                    TimestampUtc = now.AddMinutes(-1),
                    AgentType = AiAgentType.Analyzer.ToString(),
                    Context = """{"detectedPatternSymbol":"QQQ","detectedPatternName":"DoubleBottom"}""",
                    ResultJson = """{"result":{"action":"REJECT","confidence":0.94,"marketRegime":"pullback"}}"""
                });

                await db.SaveChangesAsync();
            }

            var service = new PatternAnalysisDashboardService(factory);
            var dashboard = await service.GetAnalysisAsync(new PatternAnalysisFilter
            {
                Pattern = "Hammer",
                Symbol = "SPY"
            });

            var row = Assert.Single(dashboard.Rows);
            Assert.Equal("Unknown", row.AiDecision);
            Assert.Null(row.AiConfidence);
            Assert.Empty(row.MarketRegime);

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
