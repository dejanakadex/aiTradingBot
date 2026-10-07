using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class InstrumentRolloutServiceTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task ShadowSamplePassingThresholds_AdvancesInstrumentToPaperReady()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.ShadowReady);
            await harness.SeedResearchAsync();
            await harness.SeedHealthyStreamAsync();
            await harness.SeedDecisionAsync(Guid.NewGuid(), true);
            await harness.SeedDecisionAsync(Guid.NewGuid(), true);

            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.True(result.ShadowCriteriaPassed);
            Assert.Equal(InstrumentOnboardingStatus.PaperReady, result.StatusAfter);
            Assert.Equal(InstrumentOnboardingStatus.PaperReady, harness.Registry.Snapshot.Status);
        }

        [Fact]
        public async Task PaperReadyInstrumentWithUnhealthyFeed_IsAutomaticallySuspended()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.PaperReady);
            await harness.SeedHealthyStreamAsync(healthy: false);

            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.True(result.Suspended);
            Assert.Equal(InstrumentOnboardingStatus.Suspended, result.StatusAfter);
            Assert.Contains(result.Reasons, x => x.Contains("unhealthy", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task PaperMetricsPassing_AllowsExplicitManualLiveApproval()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.PaperReady, liveExplicitlyEnabled: true);
            await harness.SeedResearchAsync();
            await harness.SeedHealthyStreamAsync();
            var signalId = Guid.NewGuid();
            await harness.SeedDecisionAsync(signalId, true);
            await harness.SeedFilledPaperOrderAsync(signalId, 100.02m, NowUtc.AddMilliseconds(-100));

            var evaluation = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", false);
            var approved = await harness.Service.ApproveLiveAsync("US-STK-SPY-SMART", new ManualLiveApprovalRequest(true, "Operator approved measured paper rollout.", 1));

            Assert.True(evaluation.PaperCriteriaPassed);
            Assert.True(evaluation.EligibleForManualLiveApproval);
            Assert.Equal(InstrumentOnboardingStatus.LiveEnabled, approved.Status);
        }

        [Fact]
        public async Task CompletedBackfillAfterRestart_ResumesCollectingAndWarmup()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.Backfilling);
            await harness.SeedResearchAsync();

            var collecting = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);
            var warming = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);
            var research = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.Equal(InstrumentOnboardingStatus.Collecting, collecting.StatusAfter);
            Assert.Equal(InstrumentOnboardingStatus.WarmingUp, warming.StatusAfter);
            Assert.Equal(InstrumentOnboardingStatus.ResearchReady, research.StatusAfter);
        }

        [Fact]
        public async Task HistoricalGaps_BlockResearchReadinessWithReason()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.Collecting);
            await harness.SeedResearchAsync(gaps: true);

            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.Equal(InstrumentOnboardingStatus.Collecting, result.StatusAfter);
            Assert.Contains(result.Reasons, x => x.Contains("gap", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task MissingLabels_BlockResearchReadinessWithReason()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.WarmingUp);
            await harness.SeedResearchAsync(labels: false);
            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.Equal(InstrumentOnboardingStatus.WarmingUp, result.StatusAfter);
            Assert.Contains(result.Reasons, x => x.Contains("labels", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(false, false, "evaluation")]
        [InlineData(true, false, "model")]
        public async Task ShadowCannotBecomePaperWithoutEvaluationAndApprovedModel(bool evaluation, bool approvedModel, string reason)
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.ShadowReady);
            await harness.SeedResearchAsync(evaluation, approvedModel);
            await harness.SeedHealthyStreamAsync();
            await harness.SeedDecisionAsync(Guid.NewGuid(), true);
            await harness.SeedDecisionAsync(Guid.NewGuid(), true);

            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.Equal(InstrumentOnboardingStatus.ShadowReady, result.StatusAfter);
            Assert.Contains(result.Reasons, x => x.Contains(reason, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task DecisionsBeforeShadowEntry_DoNotCompleteProtocol()
        {
            await using var harness = CreateHarness(InstrumentOnboardingStatus.ShadowReady);
            await harness.SeedResearchAsync();
            await harness.SeedHealthyStreamAsync();
            await harness.SeedDecisionAsync(Guid.NewGuid(), true, NowUtc.AddSeconds(-3));
            await harness.SeedDecisionAsync(Guid.NewGuid(), true);

            var result = await harness.Service.EvaluateAsync("US-STK-SPY-SMART", true);

            Assert.Equal(InstrumentOnboardingStatus.ShadowReady, result.StatusAfter);
            Assert.Equal(1, result.ShadowDecisionCount);
        }

        private static Harness CreateHarness(InstrumentOnboardingStatus status, bool liveExplicitlyEnabled = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new SimpleFactory(options);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            var registry = new FakeRegistry(status);
            var rolloutSettings = new InstrumentRolloutSettings
            {
                MinimumFinalBarsPerTimeframe = 1,
                MinimumCompleteLabelsPerStrategy = 1,
                LookbackHours = 24,
                MinimumShadowDecisions = 2,
                MinimumApprovedShadowDecisions = 1,
                MinimumPaperOrders = 1,
                MaximumPaperUnfilledRatio = 0m,
                MaximumAverageEntrySlippageBps = 3m,
                MaximumAverageFillLatencyMilliseconds = 2500,
                MaximumRecentQualityIncidents = 0
            };
            var trading = new TradingSettings { Enabled = true, LiveTradingExplicitlyEnabled = liveExplicitlyEnabled };
            var service = new InstrumentRolloutService(factory, registry, new ReadyStatusService(), Options.Create(rolloutSettings), Options.Create(trading), new FixedClock(NowUtc));
            return new Harness(connection, factory, registry, service);
        }

        private sealed class Harness : IAsyncDisposable
        {
            public Harness(SqliteConnection connection, SimpleFactory factory, FakeRegistry registry, InstrumentRolloutService service)
            {
                Connection = connection; Factory = factory; Registry = registry; Service = service;
            }
            private SqliteConnection Connection { get; }
            public SimpleFactory Factory { get; }
            public FakeRegistry Registry { get; }
            public InstrumentRolloutService Service { get; }

            public async Task SeedResearchAsync(bool evaluation = true, bool approvedModel = true, bool gaps = false, bool labels = true)
            {
                await using var db = Factory.CreateDbContext();
                db.HistoricalBackfillJobRecords.Add(new HistoricalBackfillJobRecord
                {
                    InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Timeframe = "1m",
                    Status = gaps ? HistoricalBackfillStatus.CompletedWithGaps : HistoricalBackfillStatus.Completed,
                    DesiredStartUtc = NowUtc.AddDays(-1), DesiredEndUtc = NowUtc, NextSegmentEndUtc = NowUtc.AddDays(-1),
                    GapCount = gaps ? 1 : 0, Version = 1
                });
                db.Candles.Add(new TradingBot.Persistence.Candle
                {
                    InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Timeframe = Timeframe.OneMinute,
                    TimestampUtc = NowUtc.AddMinutes(-1), ReceivedTimeUtc = NowUtc, IsFinal = true,
                    QualityStatus = MarketDataQualityStatus.Healthy, Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 100m
                });
                var labelVersion = CandidateLabelVersion.Create(new CandidateLabelingSettings());
                var featureVersion = new FeatureEngine(new CanonicalFeatureSettings()).FeatureVersion;
                var candidate = new ResearchCandidateRecord
                {
                    RecordKey = Guid.NewGuid().ToString("N"), CandidateKey = Guid.NewGuid().ToString("N"),
                    InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", StrategyId = "test",
                    EvaluatedAtUtc = NowUtc.AddMinutes(-2), FeatureVersion = featureVersion,
                    PatternVersion = PipelineContractVersions.Patterns, LabelVersion = labelVersion
                };
                db.ResearchCandidateRecords.Add(candidate);
                if (labels)
                    db.CandidateLabelRecords.Add(new CandidateLabelRecord
                    {
                        ResearchCandidate = candidate, HorizonSeconds = 60, Status = CandidateLabelStatus.Complete,
                        LabelVersion = labelVersion, InputSha256 = "valid-input", NetReturnBps = 1m,
                        WindowStartUtc = NowUtc.AddMinutes(-2), WindowEndUtc = NowUtc.AddMinutes(-1)
                    });
                if (evaluation)
                {
                    var run = new ResearchEvaluationRunRecord
                    {
                        Id = Guid.NewGuid(), RunKey = Guid.NewGuid().ToString("N"), InstrumentId = "US-STK-SPY-SMART",
                        StrategyId = "test", EvaluationVersion = ResearchEvaluationVersion.Create(new ResearchEvaluationSettings()),
                        FeatureVersion = featureVersion, PatternVersion = PipelineContractVersions.Patterns,
                        LabelVersion = labelVersion, OutputSha256 = "valid-evaluation", CompletedAtUtc = NowUtc
                    };
                    db.ResearchEvaluationRunRecords.Add(run);
                    if (approvedModel)
                    {
                        var model = new NumericalModelRecord
                        {
                            Id = Guid.NewGuid(), EvaluationRunId = run.Id, InstrumentId = "US-STK-SPY-SMART", StrategyId = "test",
                            Status = NumericalModelStatus.Approved, ApprovalReady = true, ModelVersionTag = "numerical-model-v2",
                            FeatureVersion = featureVersion, PatternVersion = PipelineContractVersions.Patterns,
                            LabelVersion = labelVersion, ModelArtifact = [1, 2, 3], InputSha256 = "valid-input"
                        };
                        model.OutputSha256 = NumericalModelService.OutputHash(model);
                        db.NumericalModelRecords.Add(model);
                    }
                }
                await db.SaveChangesAsync();
                if (approvedModel && evaluation)
                {
                    db.ChangeTracker.Clear();
                    var persisted = await db.NumericalModelRecords.SingleAsync();
                    persisted.OutputSha256 = NumericalModelService.OutputHash(persisted);
                    await db.SaveChangesAsync();
                    var reread = await db.NumericalModelRecords.AsNoTracking().SingleAsync();
                    Assert.Equal(NumericalModelService.OutputHash(reread), reread.OutputSha256);
                }
            }

            public async Task SeedHealthyStreamAsync(bool healthy = true)
            {
                await using var db = Factory.CreateDbContext();
                db.MarketDataStreamStateRecords.Add(new MarketDataStreamStateRecord
                {
                    StreamKey = "SPY:quote", InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", Kind = MarketDataEventKind.Bid,
                    Source = "test", Status = healthy ? MarketDataQualityStatus.Healthy : MarketDataQualityStatus.Stale,
                    StatusReason = healthy ? "ok" : "stale", IsHealthy = healthy, UpdatedAtUtc = NowUtc, Version = 1
                });
                await db.SaveChangesAsync();
            }

            public async Task SeedDecisionAsync(Guid signalId, bool approved, DateTime? evaluatedAtUtc = null)
            {
                await using var db = Factory.CreateDbContext();
                db.ScalpingExecutionDecisionRecords.Add(new ScalpingExecutionDecisionRecord
                {
                    SignalId = signalId, InstrumentId = "US-STK-SPY-SMART", StrategyId = "test", Symbol = "SPY",
                    EvaluatedAtUtc = evaluatedAtUtc ?? NowUtc.AddSeconds(-1), QuoteAsOfUtc = evaluatedAtUtc ?? NowUtc.AddSeconds(-1), Approved = approved,
                    SelectedPolicy = "MarketableLimit", Bid = 100m, Ask = 100.02m, Quantity = 1m,
                    ExpectedHoldingSeconds = 60, ExpectedGrossEdgeBps = 20m, EstimatedCostBps = 5m, ExpectedNetEdgeBps = 15m,
                    Reason = approved ? "ok" : "rejected", DecisionJson = "{}"
                });
                await db.SaveChangesAsync();
            }

            public async Task SeedFilledPaperOrderAsync(Guid signalId, decimal fillPrice, DateTime fillTime)
            {
                await using var db = Factory.CreateDbContext();
                var order = new OrderRecord
                {
                    BrokerOrderId = "PAPER-1", ClientOrderKey = "paper-key", IntentId = Guid.NewGuid(), Role = "Entry", Symbol = "SPY",
                    CreatedUtc = NowUtc.AddSeconds(-1), UpdatedUtc = fillTime, Status = OrderStatus.Filled, Side = "BUY", OrderType = "Limit",
                    RequestedQuantity = 1m, FilledQuantity = 1m, RemainingQuantity = 0m, AverageFillPrice = fillPrice, RawJson = "{}"
                };
                db.OrderRecords.Add(order);
                await db.SaveChangesAsync();
                db.ExecutionRecords.Add(new ExecutionRecord
                {
                    BrokerExecutionId = "EXEC-1", OrderRecordId = order.Id, Status = ExecutionStatus.Completed,
                    TimestampUtc = fillTime, Quantity = 1m, Price = fillPrice, Commission = 0.01m, RawJson = "{}"
                });
                db.SignalArbitrationRecords.Add(new SignalArbitrationRecord
                {
                    Id = Guid.NewGuid(), ArbitrationKey = "paper-arbitration", AccountId = "DU123", CorrelationId = Guid.NewGuid(), SignalId = signalId,
                    InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", StrategyId = "test", Direction = TradeDirection.Long,
                    Status = SignalArbitrationStatus.Filled, EntryBrokerOrderId = order.BrokerOrderId, ApprovedQuantity = 1m, FilledQuantity = 1m,
                    CreatedAtUtc = NowUtc.AddSeconds(-2), UpdatedAtUtc = NowUtc, ExpiresAtUtc = NowUtc.AddMinutes(1), Reason = "filled", Version = 1
                });
                await db.SaveChangesAsync();
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private sealed class SimpleFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
        {
            public TradingBotDbContext CreateDbContext() => new(options);
            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        }

        private sealed class FixedClock(DateTime utcNow) : IClock { public DateTime UtcNow { get; } = utcNow; }

        private sealed class ReadyStatusService : ITradingEngineStatusService
        {
            public BrokerReconciliationStatus Current { get; } = new()
            {
                State = TradingEngineState.Ready, TradingEnabled = true, ReconciliationCompleted = true,
                BrokerEnvironmentVerification = BrokerEnvironmentVerificationStatus.VerifiedPaper
            };
            public void SetState(TradingEngineState state, bool tradingEnabled, string message, IReadOnlyList<string>? mismatches = null,
                BrokerEnvironmentVerificationStatus brokerEnvironmentVerification = BrokerEnvironmentVerificationStatus.Unknown,
                string? connectedAccountId = null, bool reconciliationCompleted = false) { }
        }

        private sealed class FakeRegistry : IInstrumentRegistryService
        {
            public FakeRegistry(InstrumentOnboardingStatus status) => Snapshot = Make(status, 1);
            public InstrumentRegistrySnapshot Snapshot { get; private set; }
            public Task<IReadOnlyList<InstrumentRegistrySnapshot>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<InstrumentRegistrySnapshot>>([Snapshot]);
            public Task<InstrumentRegistrySnapshot?> GetAsync(string instrumentId, CancellationToken cancellationToken = default) => Task.FromResult<InstrumentRegistrySnapshot?>(Snapshot);
            public Task<InstrumentRegistrySyncResult> SynchronizeConfiguredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<InstrumentRegistrySnapshot> SetBrokerContractAsync(string instrumentId, int expectedVersion, long brokerContractId, string brokerPrimaryExchange, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<InstrumentRegistrySnapshot> TransitionAsync(string instrumentId, InstrumentOnboardingStatus expectedStatus, InstrumentOnboardingStatus targetStatus, string reason, string trigger, CancellationToken cancellationToken = default)
            {
                Assert.Equal(expectedStatus, Snapshot.Status);
                Snapshot = Make(targetStatus, Snapshot.Version + 1);
                return Task.FromResult(Snapshot);
            }
            private static InstrumentRegistrySnapshot Make(InstrumentOnboardingStatus status, int version) => new()
            {
                InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", ConfiguredEnabled = true, TradingRequested = true,
                Status = status, Version = version, StatusChangedAtUtc = NowUtc.AddSeconds(-2), BrokerContractId = 756733, BrokerPrimaryExchange = "ARCA",
                MarketDataTimeframes = ["1m"], StrategyIds = ["test"]
            };
        }
    }
}
