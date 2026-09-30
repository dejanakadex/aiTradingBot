using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class ScalpingExecutionGateTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task FreshQuoteWithEdge_ApprovesMarketableLimitAndPersistsAllPolicyEstimates()
        {
            await using var harness = CreateHarness();
            harness.ApplyQuote(NowUtc.AddMilliseconds(-100), 100m, 100.02m);

            var decision = await harness.Gate.EvaluateAsync(BuildPlan());

            Assert.True(decision.Approved, decision.Reason);
            Assert.Equal(ScalpingOrderPolicy.MarketableLimit, decision.SelectedPolicy);
            Assert.Equal(OrderType.Limit, decision.EntryOrderType);
            Assert.Equal(3, decision.Estimates.Count);
            Assert.True(decision.SelectedEstimate.ExpectedNetEdgeBps >= 3m);
            Assert.Equal(60, decision.ExpectedHoldingSeconds);
            await using var db = harness.Factory.CreateDbContext();
            var record = Assert.Single(await db.ScalpingExecutionDecisionRecords.ToListAsync());
            Assert.True(record.Approved);
            using var audit = JsonDocument.Parse(record.DecisionJson);
            var persistedDecision = audit.RootElement.GetProperty("decision");
            Assert.Equal((int)ScalpingOrderPolicy.MarketableLimit, persistedDecision.GetProperty("selectedPolicy").GetInt32());
            Assert.Equal(3, persistedDecision.GetProperty("estimates").GetArrayLength());
        }

        [Fact]
        public async Task StaleQuote_IsRejectedFailClosedAndAudited()
        {
            await using var harness = CreateHarness();
            harness.ApplyQuote(NowUtc.AddSeconds(-3), 100m, 100.02m);

            var decision = await harness.Gate.EvaluateAsync(BuildPlan());

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, x => x.Contains("stale", StringComparison.OrdinalIgnoreCase));
            await using var db = harness.Factory.CreateDbContext();
            Assert.False(Assert.Single(await db.ScalpingExecutionDecisionRecords.ToListAsync()).Approved);
        }

        [Fact]
        public async Task ExpectedMoveThatDoesNotCoverCosts_IsRejected()
        {
            await using var harness = CreateHarness(settings =>
            {
                settings.CommissionPerSideBps = 3m;
                settings.MarketableLimitSlippagePerSideBps = 3m;
                settings.SafetyBufferBps = 4m;
            });
            harness.ApplyQuote(NowUtc.AddMilliseconds(-100), 100m, 100.02m);

            var decision = await harness.Gate.EvaluateAsync(BuildPlan(expectedMovePercent: 0.10m));

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, x => x.Contains("net edge", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task PlanOutsideLatencyBudget_IsRejected()
        {
            await using var harness = CreateHarness();
            harness.ApplyQuote(NowUtc.AddMilliseconds(-100), 100m, 100.02m);

            var decision = await harness.Gate.EvaluateAsync(BuildPlan(approvedAtUtc: NowUtc.AddSeconds(-4)));

            Assert.False(decision.Approved);
            Assert.Contains(decision.RejectionReasons, x => x.Contains("latency budget", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void InvalidConfiguration_IsReported()
        {
            var settings = DefaultSettings();
            settings.MaximumQuoteAgeMilliseconds = 0;
            settings.MinimumNetEdgeBps = -1m;
            Assert.Equal(2, settings.GetValidationErrors().Count);
        }

        private static Harness CreateHarness(Action<ScalpingExecutionSettings>? configure = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new SimpleFactory(options);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();

            var trading = new TradingSettings
            {
                Enabled = true,
                OperatingMode = TradingOperatingMode.AnalysisOnly,
                Instruments =
                [
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-SPY-SMART",
                        Symbol = "SPY",
                        Enabled = true,
                        TradingEnabled = false,
                        AllowedDirections = [TradeDirection.Long],
                        StrategyIds = [PipelineContractVersions.DefaultStrategyId],
                        MarketDataTimeframes = ["1m"],
                        MaximumHoldingSeconds = 180
                    }
                ]
            };
            var latest = new LatestMarketDataService();
            var mode = new OperatingModeService(Options.Create(trading), factory, NullLogger<OperatingModeService>.Instance);
            var gateSettings = DefaultSettings();
            configure?.Invoke(gateSettings);
            var gate = new ScalpingExecutionGate(
                latest,
                mode,
                new NoopAccountService(),
                new NoopPositionService(),
                new NoopOrderExecutionService(),
                new NoopPortfolioRiskService(),
                factory,
                Options.Create(gateSettings),
                Options.Create(trading),
                Options.Create(new RiskSettings()),
                Options.Create(new IbkrSettings()),
                new FixedClock(NowUtc),
                NullLogger<ScalpingExecutionGate>.Instance);
            return new Harness(connection, factory, latest, gate);
        }

        private static ScalpingExecutionSettings DefaultSettings() => new()
        {
            MaximumQuoteAgeMilliseconds = 1500,
            MaximumBidAskSkewMilliseconds = 500,
            MaximumApprovedPlanAgeMilliseconds = 3000,
            MaximumRiskDecisionAgeMilliseconds = 5000,
            MaximumSpreadBps = 8m,
            CommissionPerSideBps = 0.5m,
            MarketableLimitSlippagePerSideBps = 0.75m,
            SafetyBufferBps = 2m,
            MinimumNetEdgeBps = 3m
        };

        private static ApprovedTradePlan BuildPlan(decimal expectedMovePercent = 0.20m, DateTime? approvedAtUtc = null)
        {
            var context = PipelineContext.Create("US-STK-SPY-SMART");
            return new ApprovedTradePlan
            {
                Pattern = new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, NowUtc.AddSeconds(-1), 0.8m, context: context),
                AiAnalysis = new AiMarketAnalysisResult
                {
                    Action = AiMarketActions.Buy,
                    Confidence = 0.8m,
                    ExpectedMovePercent = expectedMovePercent,
                    ExpectedHorizonMinutes = 1,
                    EntryMin = 99.90m,
                    EntryMax = 100.10m,
                    AnalyzedAtUtc = NowUtc.AddSeconds(-1)
                },
                StrategyDecision = new StrategyDecision
                {
                    Symbol = "SPY",
                    Approved = true,
                    EntryMin = 99.90m,
                    EntryMax = 100.10m,
                    StopPrice = 99.50m,
                    TakeProfitPrice = 100.20m,
                    Context = context
                },
                RiskDecision = new RiskDecision(RiskDecisionType.Approve, "ok", 5m, 500m, 2m, NowUtc.AddMilliseconds(-500), context: context),
                ApprovedAtUtc = approvedAtUtc ?? NowUtc.AddMilliseconds(-250)
            };
        }

        private sealed class Harness : IAsyncDisposable
        {
            public Harness(SqliteConnection connection, SimpleFactory factory, LatestMarketDataService latest, ScalpingExecutionGate gate)
            {
                Connection = connection;
                Factory = factory;
                Latest = latest;
                Gate = gate;
            }
            private SqliteConnection Connection { get; }
            public SimpleFactory Factory { get; }
            private LatestMarketDataService Latest { get; }
            public ScalpingExecutionGate Gate { get; }
            public void ApplyQuote(DateTime at, decimal bid, decimal ask)
            {
                Latest.Apply(Tick("bid", MarketDataEventKind.Bid, at, bid));
                Latest.Apply(Tick("ask", MarketDataEventKind.Ask, at, ask));
            }
            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private static CanonicalMarketDataEvent Tick(string id, MarketDataEventKind kind, DateTime at, decimal price) => new()
        {
            EventId = id,
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            Kind = kind,
            EventTimeUtc = at,
            ReceivedTimeUtc = at,
            Source = "test",
            Price = price
        };

        private sealed class FixedClock(DateTime utcNow) : IClock { public DateTime UtcNow { get; } = utcNow; }
        private sealed class SimpleFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options) : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            public TradingBot.Persistence.TradingBotDbContext CreateDbContext() => new(options);
            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        }
#pragma warning disable CS0067 // Events are required by broker abstraction test doubles.
        private sealed class NoopAccountService : IAccountService
        {
            public event Func<AccountInfo, Task>? AccountUpdated;
            public Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
        private sealed class NoopPositionService : IPositionService
        {
            public event Func<PositionDto, Task>? PositionUpdated;
            public Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
        private sealed class NoopOrderExecutionService : IOrderExecutionService
        {
            public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
            public event Func<OrderStatusDto, Task>? OrderFilled;
            public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
        private sealed class NoopPortfolioRiskService : IPortfolioRiskService
        {
            public Task CommitAsync(Guid reservationId, string? brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<PortfolioRiskStateDto> GetStateAsync(string? accountId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task ReleaseAsync(Guid reservationId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<PortfolioReservationResult> TryReserveAsync(StrategyDecision strategyDecision, AccountInfo accountInfo, IReadOnlyList<PositionDto> currentPositions, IReadOnlyList<OrderStatusDto> openOrders, PositionSizingResult proposedSizing, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
#pragma warning restore CS0067
    }
}
