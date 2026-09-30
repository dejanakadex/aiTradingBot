using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
#pragma warning disable CS0067
    public sealed class AiPipelineOptimizationTests
    {
        private static readonly DateTime MarketOpenUtc = new(2026, 8, 24, 15, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime AfterMarketCloseUtc = new(2026, 8, 24, 21, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(0.59, 0, 0, "PatternRejectedBeforeAi")]
        [InlineData(0.60, 1, 0, "CriticSkipped")]
        [InlineData(0.80, 1, 0, "CriticSkipped")]
        public async Task DeterministicPatternQualityThreshold_GatesAiAnalyzer(double quality, int expectedAnalyzerCalls, int expectedCriticCalls, string expectedRecordType)
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                analyzerResult: Analysis(AiMarketActions.Wait, 0.90m));

            await harness.Service.ProcessPatternAsync(Pattern((decimal)quality), CancellationToken.None);
            await WaitForAsync(() => harness.Analyzer.CallCount >= expectedAnalyzerCalls
                && harness.Critic.CallCount >= expectedCriticCalls
                && HasRecord(harness.Factory, expectedRecordType));

            Assert.Equal(expectedAnalyzerCalls, harness.Analyzer.CallCount);
            Assert.Equal(expectedCriticCalls, harness.Critic.CallCount);

            harness.Dispose();
        }

        [Fact]
        public async Task MarketClosed_GatesAiAnalyzerBeforeOpenAiCall()
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                analyzerResult: Analysis(AiMarketActions.Buy, 0.90m),
                nowUtc: AfterMarketCloseUtc);

            await harness.Service.ProcessPatternAsync(Pattern(0.90m, AfterMarketCloseUtc.AddMinutes(-1)), CancellationToken.None);
            await WaitForAsync(() => HasRecord(harness.Factory, "Outside configured New York trading hours"));

            Assert.Equal(0, harness.Analyzer.CallCount);
            Assert.Equal(0, harness.Critic.CallCount);
            Assert.Equal(0, harness.Strategy.CallCount);

            harness.Dispose();
        }

        [Fact]
        public async Task StaleHistoricalPattern_GatesAiAnalyzerBeforeOpenAiCall()
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                analyzerResult: Analysis(AiMarketActions.Buy, 0.90m),
                nowUtc: MarketOpenUtc);

            await harness.Service.ProcessPatternAsync(Pattern(0.90m, MarketOpenUtc.AddMinutes(-10)), CancellationToken.None);
            await WaitForAsync(() => HasRecord(harness.Factory, "Pattern is stale"));

            Assert.Equal(0, harness.Analyzer.CallCount);
            Assert.Equal(0, harness.Critic.CallCount);
            Assert.Equal(0, harness.Strategy.CallCount);

            harness.Dispose();
        }

        [Fact]
        public async Task DeterministicPatternQualityThreshold_UsesConfigurationChanges()
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.75m,
                analyzerResult: Analysis(AiMarketActions.Wait, 0.90m));

            await harness.Service.ProcessPatternAsync(Pattern(0.70m), CancellationToken.None);
            await WaitForAsync(() => HasRecord(harness.Factory, "PatternRejectedBeforeAi"));

            Assert.Equal(0, harness.Analyzer.CallCount);
            harness.Dispose();
        }

        [Theory]
        [InlineData("WAIT", 0.95, false)]
        [InlineData("REJECT", 0.95, false)]
        [InlineData("BUY", 0.59, false)]
        [InlineData("BUY", 0.90, true)]
        public async Task CriticRunsOnlyForActionableHighConfidenceBuy(string action, double confidence, bool expectedCritic)
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                minimumAiConfidence: 0.60m,
                analyzerResult: Analysis(action, (decimal)confidence));

            await harness.Service.ProcessPatternAsync(Pattern(0.80m), CancellationToken.None);
            await WaitForAsync(() => expectedCritic
                ? harness.Critic.CallCount == 1
                : harness.Analyzer.CallCount == 1);

            Assert.Equal(1, harness.Analyzer.CallCount);
            Assert.Equal(expectedCritic ? 1 : 0, harness.Critic.CallCount);
            Assert.Equal(expectedCritic ? 1 : 0, harness.Strategy.CallCount);

            harness.Dispose();
        }

        [Theory]
        [InlineData("NO TRADE: timeout")]
        [InlineData("NO TRADE: invalid structured analyzer output")]
        public async Task CriticDoesNotRunWhenAnalyzerReturnsSafeFallback(string fallbackReason)
        {
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                analyzerResult: AiMarketAnalysisResult.NoTrade(fallbackReason));

            await harness.Service.ProcessPatternAsync(Pattern(0.80m), CancellationToken.None);
            await WaitForAsync(() => harness.Analyzer.CallCount == 1);

            Assert.Equal(1, harness.Analyzer.CallCount);
            Assert.Equal(0, harness.Critic.CallCount);

            harness.Dispose();
        }

        [Fact]
        public async Task ApprovedLocalNumericalModel_BypassesNetworkAnalyzersInEntryPath()
        {
            var prediction = new NumericalModelPrediction(
                Guid.NewGuid(), 3, 0.82m, 0.65m, 3m, 60, true,
                "Approved local model probability passed the threshold.");
            var harness = CreateHarness(
                minimumPatternQualityForAiAnalysis: 0.60m,
                analyzerResult: Analysis(AiMarketActions.Buy, 0.90m),
                numericalPrediction: prediction);

            await harness.Service.ProcessPatternAsync(Pattern(0.80m), CancellationToken.None);
            await WaitForAsync(() => harness.Strategy.CallCount == 1
                && HasRecord(harness.Factory, "TradeSetupScoredByNumericalModel"));

            var numericalModel = Assert.IsType<FakeNumericalModel>(harness.NumericalModel);
            Assert.Equal(1, numericalModel.CallCount);
            Assert.Equal(PipelineContractVersions.Features, numericalModel.LastRequest?.FeatureVersion);
            Assert.Equal(0, harness.Analyzer.CallCount);
            Assert.Equal(0, harness.Critic.CallCount);

            harness.Dispose();
        }

        [Fact]
        public async Task RiskHistory_LoadsOnlyTradesClosedInCurrentNewYorkSession()
        {
            var harness = CreateHarness(0.60m, Analysis(AiMarketActions.Wait, 0.90m));
            await using (var db = harness.Factory.CreateDbContext())
            {
                db.Trades.AddRange(
                    new TradingBot.Persistence.Trade { Symbol = "SPY", OpenedUtc = MarketOpenUtc.AddMinutes(-30), ClosedUtc = MarketOpenUtc.AddMinutes(-5), Size = 1m, EntryPrice = 100m, ExitPrice = 99m },
                    new TradingBot.Persistence.Trade { Symbol = "SPY", OpenedUtc = MarketOpenUtc.AddDays(-1), ClosedUtc = MarketOpenUtc.AddDays(-1).AddMinutes(5), Size = 1m, EntryPrice = 100m, ExitPrice = 90m });
                await db.SaveChangesAsync();
            }

            var trades = await harness.Service.LoadCurrentTradingSessionTradesAsync(CancellationToken.None);

            var trade = Assert.Single(trades);
            Assert.Equal(99m, trade.ExitPrice);
            Assert.Equal(TradeStatus.Closed, trade.Status);
            harness.Dispose();
        }

        [Fact]
        public void RiskAccountSelection_UsesConfiguredPaperAccount()
        {
            var harness = CreateHarness(
                0.60m,
                Analysis(AiMarketActions.Wait, 0.90m),
                operatingMode: TradingOperatingMode.PaperTrading,
                paperAccountId: "PAPER123");

            Assert.Equal("PAPER123", harness.Service.ResolveAccountId());
            harness.Dispose();
        }

        private static Harness CreateHarness(
            decimal minimumPatternQualityForAiAnalysis,
            AiMarketAnalysisResult analyzerResult,
            decimal minimumAiConfidence = 0.60m,
            DateTime? nowUtc = null,
            TradingOperatingMode operatingMode = TradingOperatingMode.AnalysisOnly,
            string? paperAccountId = null,
            NumericalModelPrediction? numericalPrediction = null)
        {
            var factory = CreateInMemoryFactory(out var connection);
            var eventBus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
            var pipeline = new TradePipelineChannel();
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Ready, true, "ready");

            var snapshot = new FakeSnapshotService();
            var analyzer = new FakeAnalyzer(analyzerResult);
            var critic = new FakeCritic();
            var strategy = new FakeStrategy();
            var risk = new FakeRisk();
            var numericalModel = numericalPrediction == null ? null : new FakeNumericalModel(numericalPrediction);

            var services = new ServiceCollection();
            services.AddSingleton<IMarketSnapshotService>(snapshot);
            services.AddSingleton<IAiMarketAnalyzer>(analyzer);
            services.AddSingleton<IAiTradeCritic>(critic);
            services.AddSingleton<IStrategyEngine>(strategy);
            services.AddSingleton<IRiskEngine>(risk);
            services.AddSingleton<IAccountService>(new FakeAccountService());
            services.AddSingleton<IPositionService>(new FakePositionService());
            services.AddSingleton<IOrderExecutionService>(new FakeOrderExecutionService());
            if (numericalModel != null)
            {
                services.AddSingleton<INumericalModelService>(numericalModel);
            }
            var provider = services.BuildServiceProvider();

            var service = new PatternDecisionBackgroundService(
                eventBus,
                pipeline,
                status,
                new PatternQualityGate(Options.Create(new PatternDetectorOptions
                {
                    MinimumTradeSetupQuality = minimumPatternQualityForAiAnalysis,
                    MinimumOneMinuteCandlesForTradeSetup = 1,
                    MinimumFiveMinuteCandlesForTradeSetup = 1,
                    MinimumFifteenMinuteCandlesForTradeSetup = 1
                })),
                provider,
                Options.Create(new TradingSettings
                {
                    Enabled = true,
                    OperatingMode = operatingMode,
                    MinimumPatternQualityForAiAnalysis = (double)minimumPatternQualityForAiAnalysis,
                    MinimumAiConfidence = minimumAiConfidence,
                    MaximumCandleAgeSeconds = 300,
                    TradingStartHourNewYork = 9,
                    TradingStartMinuteNewYork = 30,
                    TradingEndHourNewYork = 16,
                    TradingEndMinuteNewYork = 0
                }),
                Options.Create(new IbkrSettings { AccountId = "DU123", PaperAccountId = paperAccountId }),
                factory,
                new FixedClock(nowUtc ?? MarketOpenUtc),
                NullLogger<PatternDecisionBackgroundService>.Instance,
                numericalModelSettings: numericalModel == null
                    ? null
                    : Options.Create(new NumericalModelSettings { UseApprovedModelForEntry = true }));

            return new Harness(service, eventBus, factory, connection, analyzer, critic, strategy, numericalModel);
        }

        private static bool HasRecord(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory, string recordType)
        {
            using var db = factory.CreateDbContext();
            return db.AiAnalysisRecords.AsNoTracking().Any(r => r.ResultJson.Contains(recordType));
        }

        private static async Task WaitForAsync(Func<bool> condition)
        {
            var timeout = Stopwatch.StartNew();
            while (!condition())
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("Timed out waiting for the background pipeline test condition.");
                }

                await Task.Delay(25);
            }
        }

        private static PatternCandidate Pattern(decimal confidence, DateTime? detectedAtUtc = null)
        {
            return new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, detectedAtUtc ?? MarketOpenUtc, confidence);
        }

        private static AiMarketAnalysisResult Analysis(string action, decimal confidence)
        {
            return new AiMarketAnalysisResult
            {
                Action = action,
                Confidence = confidence,
                PatternQuality = 0.8m,
                MarketRegime = "pullback in uptrend",
                ExpectedMovePercent = 1m,
                ExpectedHorizonMinutes = 15,
                EntryMin = 100m,
                EntryMax = 101m,
                InvalidationPrice = 98m,
                Reason = "test"
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

        private sealed record Harness(
            PatternDecisionBackgroundService Service,
            TradingEventBus EventBus,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> Factory,
            SqliteConnection Connection,
            FakeAnalyzer Analyzer,
            FakeCritic Critic,
            FakeStrategy Strategy,
            FakeNumericalModel? NumericalModel) : IDisposable
        {
            public void Dispose()
            {
                EventBus.Dispose();
                Connection.Dispose();
            }
        }

        private sealed class FakeSnapshotService : IMarketSnapshotService
        {
            public Task<MarketSnapshot> BuildSnapshotAsync(string symbol, decimal? currentPrice = null, decimal? spread = null, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new MarketSnapshot
                {
                    Symbol = symbol,
                    CreatedAtUtc = DateTime.UtcNow,
                    CurrentPrice = 100m,
                    OneMinute = BuildContext(Timeframe.OneMinute, 1),
                    FiveMinutes = BuildContext(Timeframe.FiveMinutes, 5),
                    FifteenMinutes = BuildContext(Timeframe.FifteenMinutes, 15)
                });
            }

            private static MarketTimeframeSnapshot BuildContext(Timeframe timeframe, int minutes)
            {
                var candles = Enumerable.Range(0, 25)
                    .Select(i => new Candle("SPY", timeframe, MarketOpenUtc.AddMinutes((i - 24) * minutes), 100m + i, 101m + i, 99m + i, 100.5m + i, 1000m))
                    .ToArray();

                return new MarketTimeframeSnapshot
                {
                    Timeframe = timeframe,
                    Interpretation = timeframe.ToString(),
                    RecentCandles = candles,
                    Features = new MarketFeatures
                    {
                        FeatureVersion = PipelineContractVersions.Features,
                        InstrumentId = "SPY",
                        Symbol = "SPY",
                        TimestampUtc = MarketOpenUtc,
                        AsOfUtc = MarketOpenUtc,
                        Timeframe = timeframe,
                        SampleCount = candles.Length,
                        NormalizedLiquidity = 0.8m,
                        AtrToPriceRatio = 0.2m,
                        SpreadBps = 1m,
                        Regime = MarketRegime.Trending
                    },
                    Trend = new TrendInformation { Direction = 1, Label = "Up" },
                    Volatility = new VolatilityContext { Volatility = 0.01m }
                };
            }
        }

        private sealed class FakeAnalyzer : IAiMarketAnalyzer
        {
            private readonly AiMarketAnalysisResult _result;
            public int CallCount { get; private set; }

            public FakeAnalyzer(AiMarketAnalysisResult result)
            {
                _result = result;
            }

            public Task<AiMarketAnalysisResult> AnalyzeAsync(MarketSnapshot snapshot, PatternCandidate? detectedPattern = null, CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class FakeCritic : IAiTradeCritic
        {
            public int CallCount { get; private set; }

            public Task<AiTradeCriticResult> CritiqueAsync(MarketSnapshot snapshot, PatternCandidate pattern, AiMarketAnalysisResult analysis, CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(new AiTradeCriticResult { Approved = true, Confidence = 0.9m, Reason = "approved" });
            }
        }

        private sealed class FakeNumericalModel : INumericalModelService
        {
            private readonly NumericalModelPrediction _prediction;

            public FakeNumericalModel(NumericalModelPrediction prediction)
            {
                _prediction = prediction;
            }

            public int CallCount { get; private set; }
            public NumericalModelPredictionRequest? LastRequest { get; private set; }

            public Task<NumericalModelPrediction> PredictAsync(NumericalModelPredictionRequest request, CancellationToken cancellationToken = default)
            {
                CallCount++;
                LastRequest = request;
                return Task.FromResult(_prediction);
            }

            public Task<NumericalModelSnapshot> TrainAsync(NumericalModelTrainingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<NumericalModelSnapshot> DecideAsync(Guid id, NumericalModelDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<NumericalModelSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IReadOnlyList<NumericalModelSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class FakeStrategy : IStrategyEngine
        {
            public int CallCount { get; private set; }

            public Task<StrategyDecision> EvaluateAsync(MarketSnapshot snapshot, PatternCandidate pattern, AiMarketAnalysisResult analysis, AiTradeCriticResult critic, CancellationToken cancellationToken = default)
            {
                CallCount++;
                return Task.FromResult(new StrategyDecision
                {
                    Symbol = pattern.Symbol,
                    Approved = false,
                    RejectionReasons = new[] { "test stops before risk" }
                });
            }
        }

        private sealed class FakeRisk : IRiskEngine
        {
            public Task<RiskDecision> EvaluateAsync(StrategyDecision strategyDecision, AccountInfo accountInfo, IReadOnlyList<PositionDto> currentPositions, IReadOnlyList<Trade> todaysCompletedTrades, IReadOnlyList<OrderStatusDto> openOrders, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class FakeAccountService : IAccountService
        {
            public event Func<AccountInfo, Task>? AccountUpdated;

            public Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new AccountInfo { AccountId = accountId, NetLiquidation = 100000m, BuyingPower = 100000m });
            }
        }

        private sealed class FakePositionService : IPositionService
        {
            public event Func<PositionDto, Task>? PositionUpdated;

            public Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default)
            {
                return Task.FromResult<IEnumerable<PositionDto>>(Array.Empty<PositionDto>());
            }
        }

        private sealed class FakeOrderExecutionService : IOrderExecutionService
        {
            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;

            public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<OrderStatusDto>>(Array.Empty<OrderStatusDto>());
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow)
            {
                UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            }

            public DateTime UtcNow { get; }
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
