using System;
using System.Linq;
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
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class StrategyEngineTests
    {
        private static readonly DateTime MarketOpenUtc = new(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task EvaluateAsync_CreatesTradeSignalWhenAllRulesPass()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var pattern = BuildPattern();

            var decision = await engine.EvaluateAsync(BuildSnapshot(), pattern, BuildAnalysis(), BuildCritic());

            Assert.True(decision.Approved);
            Assert.Empty(decision.RejectionReasons);
            Assert.NotNull(decision.TradeSignal);
            Assert.Equal(TradeAction.Enter, decision.TradeSignal!.Action);
            Assert.Equal(1m, decision.TradeSignal.SuggestedQuantity);
            Assert.Equal(100m, decision.EntryMin);
            Assert.Equal(101m, decision.EntryMax);
            Assert.Equal(98m, decision.StopPrice);
            Assert.Equal(107.06m, Math.Round(decision.TakeProfitPrice!.Value, 2));
            Assert.True(decision.ExpectedRewardRiskRatio >= 2m);
            Assert.Same(pattern.Context, decision.Context);

            await AssertPersistedDecision(factory, "strategyDecision", "\"approved\":true", pattern.Context.CorrelationId.ToString());
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsLowAiConfidence()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var analysis = BuildAnalysis(confidence: 0.4m);

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), analysis, BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("AI confidence"));
            await AssertPersistedDecision(factory, "AI confidence", "\"approved\":false");
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsLowPatternQuality()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var analysis = BuildAnalysis(patternQuality: 0.4m);

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), analysis, BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Pattern quality"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsWhenCriticDoesNotApprove()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var critic = BuildCritic(approved: false, reason: "large spread and weak structure");

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), BuildAnalysis(), critic);

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("AI critic rejected"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsPoorRewardRisk()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var analysis = BuildAnalysis(expectedMovePercent: 1m);

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), analysis, BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Reward/risk ratio"));
            Assert.True(decision.ExpectedRewardRiskRatio < 2m);
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsLargeSpread()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var snapshot = BuildSnapshot(spread: 0.12m);

            var decision = await engine.EvaluateAsync(snapshot, BuildPattern(), BuildAnalysis(), BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Spread"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsDisallowedMarketRegime()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var analysis = BuildAnalysis(marketRegime: "volatile breakdown");

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), analysis, BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Market regime"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsOutsideTradingHours()
        {
            var outsideHoursUtc = new DateTime(2026, 8, 24, 23, 0, 0, DateTimeKind.Utc);
            var engine = CreateEngine(out var factory, out var connection, settings: new TradingSettings
            {
                MinimumAiConfidence = 0.6m,
                MinimumPatternQuality = 0.6m,
                MinimumRewardRiskRatio = 2m,
                MaximumSpread = 0.05m,
                AllowedMarketRegimes = new[] { "pullback in uptrend" }
            }, clock: new FixedClock(outsideHoursUtc));

            var decision = await engine.EvaluateAsync(BuildSnapshot(outsideHoursUtc), BuildPattern(outsideHoursUtc), BuildAnalysis(), BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("outside allowed trading hours"));
            connection.Dispose();
        }

        [Theory]
        [InlineData("2026-01-15T14:29:00Z")]
        [InlineData("2026-03-09T13:29:00Z")]
        [InlineData("2026-11-02T14:29:00Z")]
        public async Task EvaluateAsync_RejectsOneMinuteBeforeNyMarketOpenAcrossDst(string timestamp)
        {
            var now = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            var engine = CreateEngine(out var factory, out var connection, clock: new FixedClock(now));

            var decision = await engine.EvaluateAsync(BuildSnapshot(now), BuildPattern(now), BuildAnalysis(), BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("outside allowed trading hours"));
            connection.Dispose();
        }

        [Theory]
        [InlineData("2026-01-15T14:30:00Z")]
        [InlineData("2026-03-09T13:30:00Z")]
        [InlineData("2026-11-02T14:30:00Z")]
        public async Task EvaluateAsync_AllowsNyMarketOpenAcrossDst(string timestamp)
        {
            var now = DateTime.Parse(timestamp, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
            var engine = CreateEngine(out var factory, out var connection, clock: new FixedClock(now));

            var decision = await engine.EvaluateAsync(BuildSnapshot(now), BuildPattern(now), BuildAnalysis(), BuildCritic());

            Assert.True(decision.Approved);
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsStaleSnapshotAndCandle()
        {
            var engine = CreateEngine(out var factory, out var connection, clock: new FixedClock(MarketOpenUtc));
            var staleAt = MarketOpenUtc.AddMinutes(-10);

            var decision = await engine.EvaluateAsync(BuildSnapshot(staleAt), BuildPattern(MarketOpenUtc), BuildAnalysis(), BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Market snapshot is stale"));
            Assert.Contains(decision.RejectionReasons, r => r.Contains("one-minute candle is stale"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsExtremeVolatility()
        {
            var engine = CreateEngine(out var factory, out var connection, clock: new FixedClock(MarketOpenUtc));
            var snapshot = BuildSnapshot(MarketOpenUtc, volatility: 0.2m);

            var decision = await engine.EvaluateAsync(snapshot, BuildPattern(MarketOpenUtc), BuildAnalysis(), BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("volatility"));
            connection.Dispose();
        }

        [Fact]
        public async Task EvaluateAsync_RejectsInvalidEntryZoneAndStop()
        {
            var engine = CreateEngine(out var factory, out var connection);
            var analysis = BuildAnalysis(entryMin: 101m, entryMax: 100m, invalidationPrice: 102m);

            var decision = await engine.EvaluateAsync(BuildSnapshot(), BuildPattern(), analysis, BuildCritic());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Invalid entry zone"));
            Assert.Contains(decision.RejectionReasons, r => r.Contains("Invalid stop level"));
            connection.Dispose();
        }

        private static StrategyEngine CreateEngine(
            out IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory,
            out SqliteConnection connection,
            TradingSettings? settings = null,
            IClock? clock = null)
        {
            factory = CreateInMemoryFactory(out connection);
            settings ??= new TradingSettings
            {
                MinimumAiConfidence = 0.6m,
                MinimumPatternQuality = 0.6m,
                MinimumRewardRiskRatio = 2m,
                MaximumSpread = 0.05m,
                AllowedMarketRegimes = new[] { "pullback in uptrend" },
                TradingStartHourUtc = 0,
                TradingEndHourUtc = 0
            };

            return new StrategyEngine(Options.Create(settings), factory, clock ?? new FixedClock(MarketOpenUtc), NullLogger<StrategyEngine>.Instance);
        }

        private static async Task AssertPersistedDecision(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, params string[] expectedFragments)
        {
            await using var db = factory.CreateDbContext();
            var record = Assert.Single(await db.StrategyDecisionRecords.ToListAsync());
            foreach (var fragment in expectedFragments)
            {
                Assert.Contains(fragment, record.DecisionJson);
            }
        }

        private static MarketSnapshot BuildSnapshot(DateTime? timestamp = null, decimal? spread = 0.02m, decimal volatility = 0.01m)
        {
            var at = timestamp ?? MarketOpenUtc;
            return new MarketSnapshot
            {
                Symbol = "SPY",
                CreatedAtUtc = at,
                CurrentPrice = 100.5m,
                Spread = spread,
                OneMinute = new MarketTimeframeSnapshot
                {
                    Timeframe = Timeframe.OneMinute,
                    RecentCandles = new[]
                    {
                        new Candle("SPY", Timeframe.OneMinute, at, 100m, 101m, 99m, 100.5m, 1000m)
                    },
                    SupportResistanceCandidates = new[]
                    {
                        new SupportResistanceCandidate { Kind = "Support", Price = 98m, Source = "test" },
                        new SupportResistanceCandidate { Kind = "Resistance", Price = 108m, Source = "test" }
                    },
                    Volatility = new VolatilityContext { RecentLow = 98m, RecentHigh = 108m, Atr = 1m, Volatility = volatility }
                },
                FiveMinutes = MarketTimeframeSnapshot.Empty(Timeframe.FiveMinutes, "Trading setup / pullback context"),
                FifteenMinutes = MarketTimeframeSnapshot.Empty(Timeframe.FifteenMinutes, "Broader market direction")
            };
        }

        private static PatternCandidate BuildPattern(DateTime? detectedAt = null)
        {
            return new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, detectedAt ?? MarketOpenUtc, 0.8m, new[] { 98m, 101m, 108m });
        }

        private static AiMarketAnalysisResult BuildAnalysis(
            string action = AiMarketActions.Buy,
            decimal confidence = 0.8m,
            decimal patternQuality = 0.8m,
            string marketRegime = "pullback in uptrend",
            decimal expectedMovePercent = 6m,
            decimal entryMin = 100m,
            decimal entryMax = 101m,
            decimal invalidationPrice = 98m)
        {
            return new AiMarketAnalysisResult
            {
                Action = action,
                Confidence = confidence,
                PatternQuality = patternQuality,
                MarketRegime = marketRegime,
                ExpectedMovePercent = expectedMovePercent,
                ExpectedHorizonMinutes = 30,
                EntryMin = entryMin,
                EntryMax = entryMax,
                InvalidationPrice = invalidationPrice,
                Reason = "test analysis"
            };
        }

        private static AiTradeCriticResult BuildCritic(bool approved = true, string reason = "no material rejection flags")
        {
            return new AiTradeCriticResult
            {
                Approved = approved,
                Confidence = 0.8m,
                RiskFlags = Array.Empty<string>(),
                Reason = reason
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
                UtcNow = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            }

            public DateTime UtcNow { get; }
        }
    }
}
