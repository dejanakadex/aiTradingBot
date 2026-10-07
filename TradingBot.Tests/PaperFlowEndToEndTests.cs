using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Trainers.LightGbm;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class PaperFlowEndToEndTests
{
    private static readonly DateTime MarketTime = new(2026, 8, 24, 14, 0, 0, DateTimeKind.Utc);
    private const string InstrumentId = "US-STK-SPY-SMART";
    private const string StrategyId = "deterministic-patterns";

    [Theory]
    [InlineData("stop")]
    [InlineData("target")]
    public async Task OnboardedModelSignal_ClosesProtectedPaperTrade(string exitKind)
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        await flow.ExecuteAsync(plan);
        var entry = await flow.EntryAsync();
        Assert.Equal(PortfolioReservationStatus.Committed, (await flow.ReservationAsync()).Status);
        Assert.Equal(3, await flow.OrderCountAsync());

        await flow.Broker.FillOpenOrderAsync(entry.BrokerOrderId, plan.RiskDecision.ApprovedQuantity, 100m);
        var exit = await flow.ExitAsync();
        Assert.Equal(plan.RiskDecision.ApprovedQuantity, exit.ProtectedQuantity);
        Assert.Equal(plan.RiskDecision.ApprovedQuantity, (await flow.Broker.GetPositionsAsync("DU123")).Single(x => x.Symbol == "SPY").Quantity);

        var childId = exitKind == "stop" ? exit.ProtectiveStopBrokerOrderId : exit.TakeProfitBrokerOrderId;
        await flow.Broker.FillOpenOrderAsync(childId, plan.RiskDecision.ApprovedQuantity,
            exitKind == "stop" ? 98m : 106m);
        await flow.Broker.PublishPendingCancellationsAsync();

        exit = await flow.ExitAsync();
        Assert.Equal(ExitManagementState.Closed, exit.State);
        Assert.Equal(exit.FilledQuantity, exit.ExitedQuantity);
        Assert.Equal(0m, (await flow.Broker.GetPositionsAsync("DU123")).Single(x => x.Symbol == "SPY").Quantity);
        var allocation = await flow.ArbitrationAsync();
        Assert.Equal(SignalArbitrationStatus.Closed, allocation.Status);
        Assert.Equal(allocation.FilledQuantity, allocation.ExitedQuantity);
        Assert.Equal(plan.Context.SignalId, exit.SignalId);
        Assert.Equal(plan.Context.SignalId, (await flow.ArbitrationAsync()).SignalId);
        Assert.Equal(plan.RiskDecision.ReservationId, (await flow.ReservationAsync()).Id);
        Assert.Equal(PortfolioReservationStatus.Released, (await flow.ReservationAsync()).Status);
        Assert.Equal(0m, (await flow.Portfolio.GetStateAsync("DU123")).ReservedGrossExposure);
        Assert.Equal(exitKind == "stop" ? OrderStatus.Filled : OrderStatus.Cancelled,
            (await flow.OrderAsync(exit.ProtectiveStopBrokerOrderId)).Status);
        await using var auditDb = flow.Factory.CreateDbContext();
        Assert.True(await auditDb.SignalArbitrationAuditRecords.CountAsync(x => x.ArbitrationId == allocation.Id) >= 4);
        Assert.True(await auditDb.PortfolioRiskReservationAuditRecords.CountAsync(x => x.ReservationId == plan.RiskDecision.ReservationId) >= 3);
        Assert.Equal(2, await auditDb.ExecutionRecords.CountAsync());
        Assert.Equal(3, await auditDb.VirtualAllocationOrderRecords.CountAsync(x => x.ArbitrationId == allocation.Id));
        Assert.NotNull((await auditDb.Trades.SingleAsync()).ClosedUtc);
    }

    [Fact]
    public async Task Deadline_CancelsProtectionBeforeSubmittingAndFillingManagedExit()
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        await flow.ExecuteAsync(plan);
        await flow.Broker.FillOpenOrderAsync((await flow.EntryAsync()).BrokerOrderId,
            plan.RiskDecision.ApprovedQuantity, 100m);
        var protectedPosition = await flow.ExitAsync();
        Assert.NotNull(protectedPosition.ExitDeadlineUtc);

        await flow.Exit.ProcessExpiredPositionsAsync(protectedPosition.ExitDeadlineUtc!.Value.AddSeconds(1));
        Assert.Equal(ExitManagementState.ExitCancelPending, (await flow.ExitAsync()).State);
        Assert.Equal(3, flow.Broker.SubmittedRequests.Count);
        await flow.Broker.PublishPendingCancellationsAsync();
        var timedExit = await flow.ExitAsync();
        Assert.Equal(ExitManagementState.ExitSubmitted, timedExit.State);
        Assert.Equal(4, flow.Broker.SubmittedRequests.Count);
        Assert.Equal(OrderStatus.Cancelled, (await flow.OrderAsync(timedExit.ProtectiveStopBrokerOrderId)).Status);
        Assert.Equal(OrderStatus.Cancelled, (await flow.OrderAsync(timedExit.TakeProfitBrokerOrderId)).Status);

        await flow.Broker.FillOpenOrderAsync(timedExit.ExitBrokerOrderId,
            plan.RiskDecision.ApprovedQuantity, 100m);
        Assert.Equal(ExitManagementState.Closed, (await flow.ExitAsync()).State);
        Assert.Equal(SignalArbitrationStatus.Closed, (await flow.ArbitrationAsync()).Status);
        Assert.Equal(PortfolioReservationStatus.Released, (await flow.ReservationAsync()).Status);
        Assert.Equal(0m, (await flow.Portfolio.GetStateAsync("DU123")).ReservedGrossExposure);
        Assert.Equal(0m, (await flow.Broker.GetPositionsAsync("DU123")).Single().Quantity);
    }

    [Fact]
    public async Task PartialEntryFill_ProtectsOnlyFilledSharesAndClosesAfterCancelledRemainder()
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        await flow.ExecuteAsync(plan);
        var half = plan.RiskDecision.ApprovedQuantity / 2m;
        await flow.Broker.FillOpenOrderAsync((await flow.EntryAsync()).BrokerOrderId, half, 100m);
        var partial = await flow.ExitAsync();
        Assert.Equal(half, partial.ProtectedQuantity);
        Assert.Equal(SignalArbitrationStatus.PartiallyFilled, (await flow.ArbitrationAsync()).Status);
        await flow.Broker.FillOpenOrderAsync(partial.TakeProfitBrokerOrderId, half, 106m);
        await flow.Broker.PublishPendingCancellationsAsync();

        var finalExit = await flow.ExitAsync();
        Assert.True(finalExit.State == ExitManagementState.Closed,
            $"state={finalExit.State}, entryRemaining={finalExit.EntryRemainingQuantity}, entryCancel={finalExit.EntryCancelConfirmedUtc}, entryOrder={(await flow.EntryAsync()).Status}, fill={finalExit.FilledQuantity}, exited={finalExit.ExitedQuantity}");
        Assert.Equal(half, (await flow.ArbitrationAsync()).FilledQuantity);
        Assert.Equal(half, (await flow.ArbitrationAsync()).ExitedQuantity);
        Assert.Equal(SignalArbitrationStatus.Closed, (await flow.ArbitrationAsync()).Status);
        Assert.Equal(PortfolioReservationStatus.Released, (await flow.ReservationAsync()).Status);
        Assert.Equal(0m, (await flow.Broker.GetPositionsAsync("DU123")).Single().Quantity);
    }

    [Fact]
    public async Task RejectedEntry_LeavesNoPositionAndReleasesReservation()
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        flow.Broker.Scenario = new FakeBrokerScenario { OrderBehavior = FakeBrokerOrderBehavior.RejectOrder };
        await flow.ExecuteAsync(plan);

        Assert.Equal(OrderStatus.Rejected, (await flow.EntryAsync()).Status);
        Assert.Equal(SignalArbitrationStatus.Rejected, (await flow.ArbitrationAsync()).Status);
        Assert.Equal(PortfolioReservationStatus.Released, (await flow.ReservationAsync()).Status);
        Assert.Empty(await flow.Broker.GetPositionsAsync("DU123"));
        Assert.Empty(await flow.Broker.GetOpenOrdersAsync());
        Assert.Equal(0m, (await flow.Portfolio.GetStateAsync("DU123")).ReservedGrossExposure);
    }

    [Fact]
    public async Task DisconnectAfterBrokerAcceptance_RestartCannotDuplicateEntry()
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        flow.Broker.Scenario = new FakeBrokerScenario
        {
            OrderBehavior = FakeBrokerOrderBehavior.DisconnectAfterBrokerAcceptedOrder
        };
        await flow.ExecuteAsync(plan);
        Assert.Equal(OrderStatus.PendingBrokerConfirmation, (await flow.EntryAsync()).Status);
        Assert.Equal(PortfolioReservationStatus.Committed, (await flow.ReservationAsync()).Status);
        Assert.Single(flow.Broker.SubmittedRequests);

        await flow.Broker.ForceReconnectAsync();
        flow.Broker.Scenario = new FakeBrokerScenario();
        using var restartedOrders = new OrderManager(flow.Broker, flow.Guard, flow.Factory,
            NullLogger<OrderManager>.Instance);
        var original = flow.Broker.SubmittedRequests.Single();
        var retry = await restartedOrders.SubmitBracketOrderAsync(
            new OrderRequest(original.Symbol, OrderSide.Buy, OrderType.Limit,
                original.Quantity, plan.StrategyDecision.EntryMin!.Value, DateTime.UtcNow),
            new OrderRequest(original.Symbol, OrderSide.Sell, OrderType.Stop,
                original.Quantity, plan.StrategyDecision.StopPrice!.Value, DateTime.UtcNow),
            new OrderRequest(original.Symbol, OrderSide.Sell, OrderType.Limit,
                original.Quantity, plan.StrategyDecision.TakeProfitPrice!.Value, DateTime.UtcNow),
            plan.RiskDecision);
        Assert.True(retry.IsDuplicate);
        Assert.Single(flow.Broker.SubmittedRequests);
        Assert.Equal(PortfolioReservationStatus.Committed, (await flow.ReservationAsync()).Status);
    }

    [Fact]
    public async Task RestartedExitService_UsesPersistedDeadlineAndClosesWithoutDuplicateSell()
    {
        await using var flow = await Flow.CreateAsync();
        var plan = await flow.MakeApprovedPlanAsync();
        await flow.ExecuteAsync(plan);
        await flow.Broker.FillOpenOrderAsync((await flow.EntryAsync()).BrokerOrderId,
            plan.RiskDecision.ApprovedQuantity, 100m);
        var deadline = (await flow.ExitAsync()).ExitDeadlineUtc;
        Assert.NotNull(deadline);

        await flow.RestartExitAsync();
        Assert.Equal(deadline, (await flow.ExitAsync()).ExitDeadlineUtc);
        Assert.Equal(TradingEngineState.Ready, flow.Status.Current.State);
        await flow.Exit.ProcessExpiredPositionsAsync(deadline!.Value.AddSeconds(1));
        await flow.Exit.ProcessExpiredPositionsAsync(deadline.Value.AddSeconds(2));
        await flow.Broker.PublishPendingCancellationsAsync();
        Assert.Equal(4, flow.Broker.SubmittedRequests.Count);
        var exit = await flow.ExitAsync();
        await flow.Broker.FillOpenOrderAsync(exit.ExitBrokerOrderId, plan.RiskDecision.ApprovedQuantity, 100m);
        Assert.Equal(ExitManagementState.Closed, (await flow.ExitAsync()).State);
        Assert.Equal(PortfolioReservationStatus.Released, (await flow.ReservationAsync()).Status);
    }

    [Fact]
    public async Task SimultaneousSignalsForTwoInstruments_HaveDistinctAllocationsAndAtomicReservations()
    {
        await using var flow = await Flow.CreateAsync();
        var symbols = new[] { ("SPY", InstrumentId), ("QQQ", "US-STK-QQQ-SMART") };
        var patterns = symbols.Select(item =>
        {
            var context = PipelineContext.CreateForSignal(item.Item2,
                $"test-29|{item.Item1}|{MarketTime:O}", StrategyId);
            return new PatternCandidate(PatternType.Hammer, item.Item1, Timeframe.OneMinute,
                MarketTime, 0.8m, context: context);
        }).ToArray();
        var arbitration = await Task.WhenAll(patterns.Select(pattern =>
            flow.Arbitration.ArbitrateAsync(pattern, "DU123", Array.Empty<PositionDto>())));
        Assert.All(arbitration, result => Assert.True(result.Approved, result.Reason));
        Assert.NotEqual(arbitration[0].ArbitrationId, arbitration[1].ArbitrationId);

        var sizing = new PositionSizingResult { Quantity = 5m, PositionValue = 500m, RiskAmount = 10m };
        var account = await flow.Broker.GetAccountInfoAsync("DU123");
        var reservations = await Task.WhenAll(patterns.Select(pattern => flow.Portfolio.TryReserveAsync(
            new StrategyDecision
            {
                Symbol = pattern.Symbol, Approved = true, Context = pattern.Context,
                EntryMin = 100m, StopPrice = 98m, TakeProfitPrice = 106m
            }, account, Array.Empty<PositionDto>(), Array.Empty<OrderStatusDto>(), sizing)));
        Assert.All(reservations, result => Assert.True(result.Approved, string.Join(" ", result.RejectionReasons)));
        Assert.NotEqual(reservations[0].ReservationId, reservations[1].ReservationId);
        for (var i = 0; i < 2; i++)
            await flow.Arbitration.BindReservationAsync(arbitration[i].ArbitrationId!.Value,
                reservations[i].ReservationId!.Value, reservations[i].Quantity, reservations[i].PositionValue);
        Assert.Equal(1000m, (await flow.Portfolio.GetStateAsync("DU123")).ReservedGrossExposure);
        Assert.Equal(2, (await flow.Arbitration.GetStateAsync("DU123")).Allocations.Count);
    }

    private sealed class Flow : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _services;
        private readonly TradingSettings _trading;
        private readonly RiskSettings _riskSettings;
        private readonly FixedClock _clock = new(MarketTime);
        private readonly FeatureEngine _features = new(new CanonicalFeatureSettings());

        private Flow(SqliteConnection connection, IDbContextFactory<TradingBotDbContext> factory,
            TradingSettings trading, RiskSettings riskSettings)
        {
            _connection = connection;
            Factory = factory;
            _trading = trading;
            _riskSettings = riskSettings;
            Registry = new InstrumentRegistryService(factory, Options.Create(trading), _clock);
            Status = new TradingEngineStatusService();
            Status.SetState(TradingEngineState.Ready, true, "verified fake paper session",
                brokerEnvironmentVerification: BrokerEnvironmentVerificationStatus.VerifiedPaper,
                reconciliationCompleted: true);
            Broker = new DeterministicFakeIbkrBroker { DeferCancellationCallbacks = true };
            Mode = new OperatingModeService(Options.Create(trading), factory, NullLogger<OperatingModeService>.Instance);
            Portfolio = new PortfolioRiskService(factory, Options.Create(riskSettings), _clock,
                NullLogger<PortfolioRiskService>.Instance);
            Guard = new TradingExecutionGuard(Options.Create(trading), Mode, Status, Registry);
            Orders = new OrderManager(Broker, Guard, factory, NullLogger<OrderManager>.Instance);
            Arbitration = new SignalArbitrationService(factory, Portfolio, Broker,
                Options.Create(new SignalArbitrationSettings()), _clock,
                NullLogger<SignalArbitrationService>.Instance);
            _services = new ServiceCollection()
                .AddSingleton<IOrderManager>(Orders)
                .AddSingleton<IOrderModificationService>(Broker)
                .AddSingleton<IIbkrConnectionService>(Broker)
                .BuildServiceProvider();
            Exit = new ExitManagementService(Broker, _services, factory, Mode, Status, _features,
                Options.Create(new ExitStrategySettings { Mode = ExitStrategyMode.FixedBracket, MaximumHoldingSeconds = 5 }),
                Options.Create(trading), NullLogger<ExitManagementService>.Instance, Arbitration);
            Executor = new ApprovedOrderExecutionBackgroundService(new TradePipelineChannel(), Orders, Status, Mode,
                Exit, null, factory, Options.Create(new ExitStrategySettings
                { Mode = ExitStrategyMode.FixedBracket, MaximumHoldingSeconds = 5 }),
                Options.Create(trading), NullLogger<ApprovedOrderExecutionBackgroundService>.Instance,
                Portfolio, Arbitration);
        }

        public IDbContextFactory<TradingBotDbContext> Factory { get; }
        public InstrumentRegistryService Registry { get; }
        public TradingEngineStatusService Status { get; }
        public DeterministicFakeIbkrBroker Broker { get; }
        public OperatingModeService Mode { get; }
        public PortfolioRiskService Portfolio { get; }
        public TradingExecutionGuard Guard { get; }
        public OrderManager Orders { get; }
        public SignalArbitrationService Arbitration { get; }
        public ExitManagementService Exit { get; private set; }
        public ApprovedOrderExecutionBackgroundService Executor { get; }

        public async Task RestartExitAsync()
        {
            Exit.Dispose();
            Exit = new ExitManagementService(Broker, _services, Factory, Mode, Status, _features,
                Options.Create(new ExitStrategySettings { Mode = ExitStrategyMode.FixedBracket, MaximumHoldingSeconds = 5 }),
                Options.Create(_trading), NullLogger<ExitManagementService>.Instance, Arbitration);
            await Exit.RestoreAsync();
        }

        public static async Task<Flow> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new Factory(options);
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var trading = new TradingSettings
            {
                Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading,
                MinimumAiConfidence = 0m, MinimumPatternQuality = 0m, MinimumRewardRiskRatio = 2m,
                MaximumSpread = 0.05m, AllowedMarketRegimes = ["pullback in uptrend"],
                TradingStartHourUtc = 0, TradingEndHourUtc = 0,
                Instruments =
                [
                    new InstrumentSettings
                    {
                        InstrumentId = InstrumentId, Symbol = "SPY", Enabled = true, TradingEnabled = true,
                        StrategyIds = [StrategyId], MarketDataTimeframes = ["1m"]
                    }
                ]
            };
            var risk = new RiskSettings
            {
                MaximumPositionValue = 1000m, MaximumRiskPerTrade = 100m, MaximumLeverage = 10m,
                MaximumGrossExposure = 10000m, MaximumNetExposure = 10000m,
                MaximumInstrumentExposure = 10000m, MaximumStrategyExposure = 10000m,
                MaximumPendingReservations = 10, MaximumPendingReservationsPerInstrument = 1,
                InstrumentCooldownSeconds = 0, MaximumOpenPositions = 10
            };
            var flow = new Flow(connection, factory, trading, risk);
            await flow.Broker.ConnectAsync();
            await flow.OnboardAsync();
            return flow;
        }

        private async Task OnboardAsync()
        {
            var sync = await Registry.SynchronizeConfiguredAsync();
            Assert.Equal(1, sync.Added);
            Assert.Equal(InstrumentOnboardingStatus.BackfillPending, sync.Instruments[0].Status);
            var historical = new HistoricalBackfillService(Factory, new HistoricalBars(), Registry,
                Options.Create(new HistoricalBackfillSettings
                {
                    PacingDelayMilliseconds = 0,
                    OneMinute = new HistoricalBackfillTimeframeSettings { LookbackDays = 1, SegmentDays = 1 }
                }), _clock, NullLogger<HistoricalBackfillService>.Instance);
            await historical.SynchronizePlanAsync();
            var job = await historical.RunNextSegmentAsync();
            Assert.NotNull(job);
            Assert.Equal(HistoricalBackfillStatus.Completed, job.Status);
            Assert.Equal(InstrumentOnboardingStatus.Collecting, (await Registry.GetAsync(InstrumentId))!.Status);
            await SeedModelAndLabelsAsync();
            await using (var db = Factory.CreateDbContext())
            {
                db.MarketDataStreamStateRecords.Add(new MarketDataStreamStateRecord
                {
                    StreamKey = "SPY:1m", InstrumentId = InstrumentId, Symbol = "SPY",
                    Kind = MarketDataEventKind.Bar, Timeframe = "1m", IsHealthy = true,
                    Status = MarketDataQualityStatus.Healthy, UpdatedAtUtc = MarketTime
                });
                await db.SaveChangesAsync();
            }
            var rollout = new InstrumentRolloutService(Factory, Registry, Status,
                Options.Create(new InstrumentRolloutSettings
                {
                    MinimumFinalBarsPerTimeframe = 50, MinimumCompleteLabelsPerStrategy = 1,
                    MinimumShadowDecisions = 1, MinimumApprovedShadowDecisions = 1
                }), Options.Create(_trading), _clock);
            Assert.Equal(InstrumentOnboardingStatus.WarmingUp,
                (await rollout.EvaluateAsync(InstrumentId, true)).StatusAfter);
            Assert.Equal(InstrumentOnboardingStatus.ResearchReady,
                (await rollout.EvaluateAsync(InstrumentId, true)).StatusAfter);
            Assert.Equal(InstrumentOnboardingStatus.ShadowReady,
                (await rollout.EvaluateAsync(InstrumentId, true)).StatusAfter);
            await using (var db = Factory.CreateDbContext())
            {
                db.ScalpingExecutionDecisionRecords.Add(new ScalpingExecutionDecisionRecord
                {
                    SignalId = Guid.NewGuid(), InstrumentId = InstrumentId, StrategyId = StrategyId,
                    Symbol = "SPY", EvaluatedAtUtc = MarketTime, QuoteAsOfUtc = MarketTime,
                    Approved = true, SelectedPolicy = "MarketableLimit", Bid = 100m, Ask = 100.02m,
                    Quantity = 1m, ExpectedHoldingSeconds = 5, DecisionJson = "{}"
                });
                await db.SaveChangesAsync();
            }
            Assert.Equal(InstrumentOnboardingStatus.PaperReady,
                (await rollout.EvaluateAsync(InstrumentId, true)).StatusAfter);
        }

        private async Task SeedModelAndLabelsAsync()
        {
            var labelVersion = CandidateLabelVersion.Create(new CandidateLabelingSettings());
            var featureVersion = _features.FeatureVersion;
            var evaluationId = Guid.NewGuid();
            await using var db = Factory.CreateDbContext();
            var candidate = new ResearchCandidateRecord
            {
                RecordKey = Guid.NewGuid().ToString("N"), CandidateKey = Guid.NewGuid().ToString("N"),
                InstrumentId = InstrumentId, Symbol = "SPY", StrategyId = StrategyId,
                PatternType = PatternType.Hammer, Direction = TradeDirection.Long,
                Timeframe = Timeframe.OneMinute, EvaluatedAtUtc = MarketTime.AddMinutes(-10),
                FeatureVersion = featureVersion, PatternVersion = PipelineContractVersions.Patterns,
                LabelVersion = labelVersion
            };
            candidate.Labels.Add(new CandidateLabelRecord
            {
                HorizonSeconds = 60, Status = CandidateLabelStatus.Complete,
                WindowStartUtc = MarketTime.AddMinutes(-10), WindowEndUtc = MarketTime.AddMinutes(-9),
                LabelVersion = labelVersion, InputSha256 = "synthetic-input", NetReturnBps = 10m
            });
            db.ResearchCandidateRecords.Add(candidate);
            db.ResearchEvaluationRunRecords.Add(new ResearchEvaluationRunRecord
            {
                Id = evaluationId, RunKey = Guid.NewGuid().ToString("N"),
                InstrumentId = InstrumentId, StrategyId = StrategyId,
                EvaluationVersion = ResearchEvaluationVersion.Create(new ResearchEvaluationSettings()),
                FeatureVersion = featureVersion, PatternVersion = PipelineContractVersions.Patterns,
                LabelVersion = labelVersion, OutputSha256 = "synthetic-evaluation",
                CompletedAtUtc = MarketTime
            });
            var model = new NumericalModelRecord
            {
                Id = Guid.NewGuid(), EvaluationRunId = evaluationId,
                InstrumentId = InstrumentId, StrategyId = StrategyId,
                Status = NumericalModelStatus.Approved, ApprovalReady = true,
                ModelVersionTag = "numerical-model-v2", FeatureVersion = featureVersion,
                PatternVersion = PipelineContractVersions.Patterns, LabelVersion = labelVersion,
                ModelArtifact = BuildModelArtifact(), ProbabilityThreshold = 0m,
                HorizonSeconds = 5, InputSha256 = "synthetic-training-input",
                CandidateHoldoutJson = JsonSerializer.Serialize(new NumericalModelMetrics(10, 10, 0.7m, 10m, 100m, 0m))
            };
            db.NumericalModelRecords.Add(model);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var persisted = await db.NumericalModelRecords.SingleAsync();
            persisted.OutputSha256 = NumericalModelService.OutputHash(persisted);
            await db.SaveChangesAsync();
        }

        public async Task<ApprovedTradePlan> MakeApprovedPlanAsync()
        {
            await using var db = Factory.CreateDbContext();
            var bars = await db.Candles.AsNoTracking()
                .Where(x => x.InstrumentId == InstrumentId && x.Timeframe == Timeframe.OneMinute)
                .OrderBy(x => x.TimestampUtc).ToArrayAsync();
            var candles = bars.Select(x => new TradingBot.Domain.Models.Candle(
                x.Symbol, x.Timeframe, x.TimestampUtc, x.Open, x.High, x.Low, x.Close, x.Volume, x.InstrumentId)).ToArray();
            var features = _features.ComputeFeatures(candles);
            var detector = new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance);
            var pattern = detector.Detect(new PatternDetectionInput
            {
                Candles = candles, StrategyId = StrategyId, FeatureVersion = _features.FeatureVersion,
                Directions = [TradeDirection.Long]
            }).Single(x => x.PatternType == PatternType.Hammer && x.DetectedAtUtc == candles[^1].TimestampUtc);
            var predictor = new NumericalModelService(Factory, Options.Create(new NumericalModelSettings()), _clock);
            var prediction = await predictor.PredictAsync(new NumericalModelPredictionRequest(
                InstrumentId, StrategyId, _features.FeatureVersion, pattern.Confidence, (int)pattern.PatternType,
                (int)pattern.Direction, (int)pattern.Timeframe, 1m, 0.2m, 2m, MarketTime));
            Assert.True(prediction.Eligible);
            Assert.True(prediction.HistoricalSelectedNetReturnBps > 0m);
            var snapshot = new TradingBot.Domain.Models.MarketSnapshot
            {
                Symbol = "SPY", CreatedAtUtc = MarketTime, CurrentPrice = 100.1m, Spread = 0.02m,
                OneMinute = new MarketTimeframeSnapshot
                {
                    Timeframe = Timeframe.OneMinute, RecentCandles = candles, Features = features,
                    Volatility = new VolatilityContext { Atr = features.Atr, RecentLow = 98m, RecentHigh = 108m },
                    SupportResistanceCandidates =
                    [
                        new SupportResistanceCandidate { Kind = "Support", Price = 98m, Source = "synthetic" },
                        new SupportResistanceCandidate { Kind = "Resistance", Price = 108m, Source = "synthetic" }
                    ]
                }
            };
            var analysis = new AiMarketAnalysisResult
            {
                Action = AiMarketActions.Buy, Confidence = 0.8m, PatternQuality = pattern.Confidence,
                MarketRegime = "pullback in uptrend", ExpectedMovePercent = 6m,
                ExpectedHorizonMinutes = 1, EntryMin = 100m, EntryMax = 100.1m,
                InvalidationPrice = 98m, Reason = "Synthetic approved local model."
            };
            var critic = new AiTradeCriticResult { Approved = true, Confidence = 0.8m, Reason = "Synthetic test approval." };
            var strategy = new StrategyEngine(Options.Create(_trading), Factory, _clock,
                NullLogger<StrategyEngine>.Instance);
            var decision = await strategy.EvaluateAsync(snapshot, pattern, analysis, critic);
            Assert.True(decision.Approved, string.Join(" ", decision.RejectionReasons));
            var arbitration = await Arbitration.ArbitrateAsync(pattern, "DU123", Array.Empty<PositionDto>());
            Assert.True(arbitration.Approved, arbitration.Reason);
            var risk = new RiskEngine(Options.Create(_riskSettings), new PositionSizer(Options.Create(_riskSettings)),
                Factory, Portfolio, _clock, NullLogger<RiskEngine>.Instance);
            var riskDecision = await risk.EvaluateAsync(decision, await Broker.GetAccountInfoAsync("DU123"),
                Array.Empty<PositionDto>(), Array.Empty<TradingBot.Domain.Models.Trade>(), Array.Empty<OrderStatusDto>());
            Assert.Equal(RiskDecisionType.Approve, riskDecision.Decision);
            Assert.NotNull(riskDecision.ReservationId);
            await Arbitration.BindReservationAsync(arbitration.ArbitrationId!.Value,
                riskDecision.ReservationId!.Value, riskDecision.ApprovedQuantity, riskDecision.PositionValue);
            return new ApprovedTradePlan
            {
                Pattern = pattern, Snapshot = snapshot, AiAnalysis = analysis, AiCriticAnalysis = critic,
                StrategyDecision = decision, RiskDecision = riskDecision,
                ArbitrationId = arbitration.ArbitrationId
            };
        }

        public async Task ExecuteAsync(ApprovedTradePlan plan)
        {
            await Executor.ExecutePlanAsync(plan, CancellationToken.None);
            if (await OrderCountAsync() == 0)
            {
                var arbitration = await ArbitrationAsync();
                var guard = await Guard.CanSubmitBrokerOrderAsync(plan.RiskDecision);
                throw new InvalidOperationException($"No broker orders: arbitration={arbitration.Status}, reason={arbitration.Reason}; guard={guard.Reason}; submitted={Broker.SubmittedRequests.Count}.");
            }
        }
        public async Task<OrderRecord> EntryAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.OrderRecords.AsNoTracking().SingleAsync(x => x.Role == "BracketEntry");
        }
        public async Task<OrderRecord> OrderAsync(string id)
        {
            await using var db = Factory.CreateDbContext();
            return await db.OrderRecords.AsNoTracking().SingleAsync(x => x.BrokerOrderId == id);
        }
        public async Task<int> OrderCountAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.OrderRecords.CountAsync();
        }
        public async Task<ExitManagementRecord> ExitAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.ExitManagementRecords.AsNoTracking().SingleAsync();
        }
        public async Task<SignalArbitrationRecord> ArbitrationAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.SignalArbitrationRecords.AsNoTracking().SingleAsync();
        }
        public async Task<PortfolioRiskReservationRecord> ReservationAsync()
        {
            await using var db = Factory.CreateDbContext();
            return await db.PortfolioRiskReservationRecords.AsNoTracking().SingleAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Exit.Dispose();
            Arbitration.Dispose();
            Orders.Dispose();
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class HistoricalBars : IMarketDataService
    {
        public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default)
        {
            var first = request.EndUtc.AddMinutes(-50);
            IEnumerable<MarketBar> bars = Enumerable.Range(0, 50).Select(index => new MarketBar
            {
                InstrumentId = request.InstrumentId, Symbol = request.Symbol, Timeframe = request.Timeframe,
                TimestampUtc = first.AddMinutes(index), ReceivedTimeUtc = MarketTime, Source = "synthetic",
                IsFinal = true, Open = 100m,
                High = index == 49 ? 100.2m : 100.1m,
                Low = index == 49 ? 98m : 99.9m,
                Close = index == 49 ? 100.1m : 100m, Volume = 100m
            });
            return Task.FromResult(bars);
        }
        public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("End-to-end test only uses historical bars.");
    }

    private static byte[] BuildModelArtifact()
    {
        var ml = new MLContext(seed: 42);
        var rows = Enumerable.Range(0, 30).Select(index => new ModelRow
        {
            PatternConfidence = index % 2 == 0 ? 0.8f : 0.4f,
            PatternType = (float)PatternType.Hammer, Direction = (float)TradeDirection.Long,
            Timeframe = (float)Timeframe.OneMinute, NormalizedLiquidity = index % 2 == 0 ? 2f : 0.2f,
            NormalizedVolatility = 0.2f, HourSin = 0f, HourCos = 1f, Label = index % 2 == 0
        });
        var data = ml.Data.LoadFromEnumerable(rows);
        var pipeline = ml.Transforms.Concatenate("Features",
                nameof(ModelRow.PatternConfidence), nameof(ModelRow.PatternType), nameof(ModelRow.Direction),
                nameof(ModelRow.Timeframe), nameof(ModelRow.NormalizedLiquidity),
                nameof(ModelRow.NormalizedVolatility), nameof(ModelRow.HourSin), nameof(ModelRow.HourCos))
            .Append(ml.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
            {
                LabelColumnName = nameof(ModelRow.Label), FeatureColumnName = "Features",
                NumberOfLeaves = 4, NumberOfIterations = 20, MinimumExampleCountPerLeaf = 2, Seed = 42
            }));
        var model = pipeline.Fit(data);
        using var stream = new MemoryStream();
        ml.Model.Save(model, data.Schema, stream);
        return stream.ToArray();
    }

    private sealed class ModelRow
    {
        public float PatternConfidence { get; set; }
        public float PatternType { get; set; }
        public float Direction { get; set; }
        public float Timeframe { get; set; }
        public float NormalizedLiquidity { get; set; }
        public float NormalizedVolatility { get; set; }
        public float HourSin { get; set; }
        public float HourCos { get; set; }
        public bool Label { get; set; }
    }

    private sealed class Factory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
    {
        public TradingBotDbContext CreateDbContext() => new(options);
        public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => new(CreateDbContext());
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
