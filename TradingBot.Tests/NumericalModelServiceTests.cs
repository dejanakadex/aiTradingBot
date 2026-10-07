using System.Text.Json;
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
    public sealed class NumericalModelServiceTests
    {
        private static readonly DateTime StartUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly string FeatureVersion = new FeatureEngine(new CanonicalFeatureSettings()).FeatureVersion;

        [Fact]
        public async Task LightGbmCandidate_BeatsConfidenceBaseline_CanBeApprovedAndScoredLocally()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();

            var draft = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));

            Assert.True(draft.ApprovalReady, string.Join(" ", draft.ApprovalReasons));
            Assert.True(draft.CandidateWalkForward.ExpectancyBps > draft.BaselineWalkForward.ExpectancyBps);
            Assert.True(draft.CandidateHoldout.ExpectancyBps > draft.BaselineHoldout.ExpectancyBps);
            var approved = await harness.Service.DecideAsync(draft.Id, new NumericalModelDecisionRequest(true, "test-reviewer", "Synthetic walk-forward and untouched holdout passed.", draft.OutputSha256));
            var prediction = await harness.Service.PredictAsync(new NumericalModelPredictionRequest(
                "US-STK-SPY-SMART", "deterministic-patterns", FeatureVersion, 0.7m, (int)PatternType.BreakoutAndRetest,
                (int)TradeDirection.Long, (int)Timeframe.OneMinute, 2m, 0.2m, 2m, StartUtc.AddDays(10)));

            Assert.Equal(NumericalModelStatus.Approved, approved.Status);
            Assert.True(prediction.Eligible);
            Assert.True(prediction.Probability >= prediction.Threshold);
        }

        [Fact]
        public async Task ParallelPredictionsReuseLoadedModelAndRollbackSwitchesItWithAudit()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            var first = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));
            await harness.Service.DecideAsync(first.Id, new NumericalModelDecisionRequest(true, "reviewer-one",
                "First model passed review.", first.OutputSha256));
            var request = new NumericalModelPredictionRequest("US-STK-SPY-SMART", "deterministic-patterns",
                FeatureVersion, 0.7m, (int)PatternType.BreakoutAndRetest, (int)TradeDirection.Long,
                (int)Timeframe.OneMinute, 2m, 0.2m, 2m, StartUtc.AddDays(10));
            var before = harness.FactoryCreateCount;
            var predictions = await Task.WhenAll(Enumerable.Range(0, 24)
                .Select(_ => harness.Service.PredictAsync(request)));
            Assert.All(predictions, prediction => Assert.Equal(first.Id, prediction.ModelId));
            Assert.Equal(before + 1, harness.FactoryCreateCount);

            var second = await harness.CloneAsDraftAsync(first.Id);
            await harness.Service.DecideAsync(second.Id, new NumericalModelDecisionRequest(true, "reviewer-two",
                "Replacement model passed review.", second.OutputSha256));
            Assert.Equal(second.Id, (await harness.Service.PredictAsync(request)).ModelId);
            await harness.Service.RollbackAsync(first.Id, new NumericalModelRollbackRequest("reviewer-three",
                "Reverting after operational review.", first.OutputSha256));
            Assert.Equal(first.Id, (await harness.Service.PredictAsync(request)).ModelId);
            var firstHistory = await harness.Service.GetDecisionHistoryAsync(first.Id);
            Assert.Equal(new[] { "Approve", "Superseded", "Rollback" }, firstHistory.Select(x => x.Action));
            Assert.Equal(new[] { "Approve", "SupersededByRollback" },
                (await harness.Service.GetDecisionHistoryAsync(second.Id)).Select(x => x.Action));
            Assert.All(firstHistory, decision => Assert.Equal(first.OutputSha256, decision.ReviewedOutputSha256));
        }

        [Fact]
        public async Task SeparateProcessStyleCacheRefreshSeesNewApproval()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            var first = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));
            await harness.Service.DecideAsync(first.Id, new NumericalModelDecisionRequest(true, "reviewer-one",
                "First approval was reviewed.", first.OutputSha256));
            var clock = new AdjustableClock(StartUtc.AddDays(10));
            var predictor = new NumericalModelService(harness.Factory,
                Options.Create(new NumericalModelSettings { CacheRefreshSeconds = 1 }), clock);
            var request = new NumericalModelPredictionRequest("US-STK-SPY-SMART", "deterministic-patterns",
                FeatureVersion, 0.7m, 1, 1, 1, 1m, 1m, 2m, StartUtc.AddDays(10));
            Assert.Equal(first.Id, (await predictor.PredictAsync(request)).ModelId);

            var second = await harness.CloneAsDraftAsync(first.Id);
            await harness.Service.DecideAsync(second.Id, new NumericalModelDecisionRequest(true, "reviewer-two",
                "Replacement approval was reviewed.", second.OutputSha256));
            clock.UtcNow = clock.UtcNow.AddSeconds(2);

            Assert.Equal(second.Id, (await predictor.PredictAsync(request)).ModelId);
        }

        [Fact]
        public async Task CurrentLabelConfigurationMustMatchApprovedModel()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            var draft = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));
            await harness.Service.DecideAsync(draft.Id, new NumericalModelDecisionRequest(true, "test-reviewer",
                "Synthetic evidence was reviewed.", draft.OutputSha256));
            var incompatible = new NumericalModelService(harness.Factory, Options.Create(new NumericalModelSettings()),
                new FixedClock(StartUtc.AddDays(10)), Options.Create(new CandidateLabelingSettings { TargetMoveBps = 20m }));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => incompatible.PredictAsync(
                new NumericalModelPredictionRequest("US-STK-SPY-SMART", "deterministic-patterns", FeatureVersion,
                    0.7m, 1, 1, 1, 1m, 1m, 2m, StartUtc.AddDays(10))));
            Assert.Contains("label configuration", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task CurrentFeatureConfigurationMustMatchApprovedModel()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            var draft = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));
            await harness.Service.DecideAsync(draft.Id, new NumericalModelDecisionRequest(true, "test-reviewer",
                "Synthetic evidence was reviewed.", draft.OutputSha256));
            var incompatible = new NumericalModelService(harness.Factory, Options.Create(new NumericalModelSettings()),
                new FixedClock(StartUtc.AddDays(10)), null,
                Options.Create(new CanonicalFeatureSettings { EmaShortPeriod = 10 }));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => incompatible.PredictAsync(
                new NumericalModelPredictionRequest("US-STK-SPY-SMART", "deterministic-patterns", FeatureVersion,
                    0.7m, 1, 1, 1, 1m, 1m, 2m, StartUtc.AddDays(10))));
            Assert.Contains("feature configuration", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task RejectionIsAuditedAndCannotBeRolledBack()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            var draft = await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));

            var rejected = await harness.Service.DecideAsync(draft.Id, new NumericalModelDecisionRequest(false,
                "test-reviewer", "Validation is not accepted.", draft.OutputSha256));

            Assert.Equal(NumericalModelStatus.Rejected, rejected.Status);
            Assert.Equal("Reject", Assert.Single(await harness.Service.GetDecisionHistoryAsync(draft.Id)).Action);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.RollbackAsync(draft.Id,
                new NumericalModelRollbackRequest("test-reviewer", "Attempt to restore rejected model.", draft.OutputSha256)));
        }

        [Fact]
        public async Task MigrationCreatesNumericalModelAndDecisionAuditTables()
        {
            var path = Path.Combine(Path.GetTempPath(), $"tradingbot-modelops-{Guid.NewGuid():N}.db");
            try
            {
                var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite($"Data Source={path}").Options;
                await using var db = new TradingBotDbContext(options);
                await db.Database.MigrateAsync();
                var connection = db.Database.GetDbConnection();
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('NumericalModelRecords','NumericalModelDecisionRecords')";
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
                await connection.CloseAsync();
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task PredictionWithoutApprovedModel_FailsClosed()
        {
            await using var harness = CreateHarness();
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.PredictAsync(new NumericalModelPredictionRequest(
                "US-STK-SPY-SMART", "deterministic-patterns", FeatureVersion, 0.7m, 1, 1, 1, 1m, 1m, 2m, StartUtc)));
        }

        [Fact]
        public async Task LegacyHorizonReturnEvaluationCannotTrainModel()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            await harness.MarkEvaluationLegacyAsync(evaluationId);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId)));
        }

        [Fact]
        public async Task ChangedLabelAfterEvaluationCannotTrainModel()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            await harness.ChangeOneLabelAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId)));
            Assert.Contains("input changed", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ExistingModelIsNotReturnedWhenFrozenEvaluationInputChanged()
        {
            await using var harness = CreateHarness();
            var evaluationId = await harness.SeedEvaluationAndLabelsAsync();
            await harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId));
            await harness.ChangeOneLabelAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                harness.Service.TrainAsync(new NumericalModelTrainingRequest(evaluationId)));
        }

        [Fact]
        public void InvalidModelConfiguration_IsReported()
        {
            var settings = new NumericalModelSettings { LearningRate = 0, ProbabilityThresholds = [0m] };
            Assert.Equal(2, settings.GetValidationErrors().Count);
        }

        private static Harness CreateHarness()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            var factory = new SimpleFactory(options);
            using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
            var settings = new NumericalModelSettings
            {
                RandomSeed = 42, NumberOfLeaves = 4, NumberOfIterations = 30, LearningRate = 0.1,
                MinimumExampleCountPerLeaf = 2, MinimumTrainingSamples = 40, MinimumValidationSelections = 5,
                MinimumHoldoutSelections = 5, MinimumWalkForwardExpectancyImprovementBps = 0.1m,
                MinimumHoldoutExpectancyImprovementBps = 0m, ProbabilityThresholds = [0.5m, 0.6m, 0.7m]
            };
            return new Harness(connection, factory, new NumericalModelService(factory, Options.Create(settings), new FixedClock(StartUtc.AddDays(10))));
        }

        private sealed class Harness : IAsyncDisposable
        {
            public Harness(SqliteConnection connection, SimpleFactory factory, NumericalModelService service) { Connection = connection; Factory = factory; Service = service; }
            private SqliteConnection Connection { get; }
            public SimpleFactory Factory { get; }
            public int FactoryCreateCount => Factory.CreateCount;
            public NumericalModelService Service { get; }

            public async Task<NumericalModelSnapshot> CloneAsDraftAsync(Guid id)
            {
                await using var db = Factory.CreateDbContext();
                var source = await db.NumericalModelRecords.AsNoTracking().SingleAsync(x => x.Id == id);
                var clone = new NumericalModelRecord
                {
                    Id = Guid.NewGuid(), ModelVersion = source.ModelVersion + 1, Status = NumericalModelStatus.Draft,
                    EvaluationRunId = Guid.NewGuid(), InstrumentId = source.InstrumentId, StrategyId = source.StrategyId,
                    HorizonSeconds = source.HorizonSeconds, Algorithm = source.Algorithm, ModelVersionTag = source.ModelVersionTag,
                    FeatureVersion = source.FeatureVersion, PatternVersion = source.PatternVersion, LabelVersion = source.LabelVersion,
                    ProbabilityThreshold = source.ProbabilityThreshold, BaselineWalkForwardJson = source.BaselineWalkForwardJson,
                    CandidateWalkForwardJson = source.CandidateWalkForwardJson, BaselineHoldoutJson = source.BaselineHoldoutJson,
                    CandidateHoldoutJson = source.CandidateHoldoutJson, ApprovalReady = source.ApprovalReady,
                    ApprovalReasonsJson = source.ApprovalReasonsJson, ModelArtifact = source.ModelArtifact,
                    InputSha256 = source.InputSha256, OutputSha256 = source.OutputSha256, CreatedAtUtc = StartUtc.AddDays(11)
                };
                db.NumericalModelRecords.Add(clone);
                await db.SaveChangesAsync();
                return (await Service.GetAsync(clone.Id))!;
            }

            public async Task MarkEvaluationLegacyAsync(Guid evaluationId)
            {
                await using var db = Factory.CreateDbContext();
                var evaluation = await db.ResearchEvaluationRunRecords.SingleAsync(x => x.Id == evaluationId);
                evaluation.LabelVersion = "labels-v1+config-legacy";
                await db.SaveChangesAsync();
            }

            public async Task ChangeOneLabelAsync()
            {
                await using var db = Factory.CreateDbContext();
                var label = await db.CandidateLabelRecords.OrderBy(x => x.Id).FirstAsync();
                label.NetReturnBps += 1m;
                await db.SaveChangesAsync();
            }

            public async Task<Guid> SeedEvaluationAndLabelsAsync()
            {
                var evaluationId = Guid.NewGuid();
                var labelVersion = CandidateLabelVersion.Create(new CandidateLabelingSettings());
                var fold = new WalkForwardFoldResult(1, StartUtc, StartUtc.AddDays(4), StartUtc.AddDays(4), StartUtc.AddDays(6), 80, 40, 0.5m, "test", null, null);
                await using var db = Factory.CreateDbContext();
                db.ResearchEvaluationRunRecords.Add(new ResearchEvaluationRunRecord
                {
                    Id = evaluationId, RunKey = Guid.NewGuid().ToString("N"), InstrumentId = "US-STK-SPY-SMART", StrategyId = "deterministic-patterns",
                    HorizonSeconds = 60, FromUtc = StartUtc, ToUtc = StartUtc.AddDays(9), HoldoutStartUtc = StartUtc.AddDays(7),
                    SourceCandidateCount = 160, EligibleCandidateCount = 160, WalkForwardFoldCount = 1, SelectedConfidenceThreshold = 0.5m,
                    SelectionReason = "test", EvaluationVersion = "evaluation-v2+config-test", FeatureVersion = FeatureVersion, PatternVersion = "patterns-v2", LabelVersion = labelVersion,
                    InputSha256 = "test", OutputSha256 = "test", PreHoldoutMetricsJson = "{}", WalkForwardMetricsJson = "{}", HoldoutMetricsJson = "{}",
                    PreHoldoutThresholdsJson = "[]", FoldsJson = JsonSerializer.Serialize(new[] { fold }), CostSensitivityJson = "[]", SegmentsJson = "[]",
                    CreatedAtUtc = StartUtc.AddDays(9), CompletedAtUtc = StartUtc.AddDays(9)
                });
                var times = Enumerable.Range(0, 120).Select(i => StartUtc.AddHours(i))
                    .Concat(Enumerable.Range(0, 40).Select(i => StartUtc.AddDays(7).AddHours(i))).ToArray();
                for (var i = 0; i < times.Length; i++)
                {
                    var winning = i % 2 == 0;
                    var candidate = new ResearchCandidateRecord
                    {
                        RecordKey = $"record-{i}", CandidateKey = $"candidate-{i}", InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                        StrategyId = "deterministic-patterns", PatternType = PatternType.BreakoutAndRetest, Direction = TradeDirection.Long,
                        Timeframe = Timeframe.OneMinute, EvaluatedAtUtc = times[i], ReferencePrice = 100m, Confidence = 0.7m,
                        Outcome = ResearchCandidateOutcome.Accepted, DecisionStage = "test", FeatureVersion = FeatureVersion, PatternVersion = "patterns-v2",
                        LabelVersion = labelVersion, MarketRegime = "Trending", NormalizedLiquidity = winning ? 2m : 0.2m, NormalizedVolatility = 0.2m,
                        HardConditionsJson = "[]", ScoreComponentsJson = "[]", ReasonsJson = "[]", MetadataJson = "{}", CreatedAtUtc = times[i], UpdatedAtUtc = times[i]
                    };
                    candidate.Labels.Add(new CandidateLabelRecord
                    {
                        HorizonSeconds = 60, Status = CandidateLabelStatus.Complete, TargetStopOutcome = winning ? TargetStopOutcome.TargetFirst : TargetStopOutcome.StopFirst,
                        WindowStartUtc = times[i], WindowEndUtc = times[i].AddMinutes(1), ObservationCount = 10, EntryPrice = 100m, ExitPrice = winning ? 100.1m : 99.9m,
                        MaximumFavorableExcursionBps = winning ? 10m : 2m, MaximumAdverseExcursionBps = winning ? 2m : 10m,
                        GrossReturnBps = winning ? 12m : -8m, EstimatedCostBps = 2m, NetReturnBps = winning ? 10m : -10m,
                        ReasonsJson = "[]", LabelVersion = labelVersion, InputSha256 = $"input-{i}", CalculatedAtUtc = times[i].AddMinutes(1)
                    });
                    db.ResearchCandidateRecords.Add(candidate);
                }
                await db.SaveChangesAsync();
                var storedLabels = await db.CandidateLabelRecords.AsNoTracking()
                    .Include(x => x.ResearchCandidate).ToArrayAsync();
                var hashRows = storedLabels.Select(x => new ResearchEvaluationObservation(
                    x.ResearchCandidateId, x.ResearchCandidate.CandidateKey, x.ResearchCandidate.InstrumentId,
                    x.ResearchCandidate.StrategyId, x.ResearchCandidate.PatternType, x.ResearchCandidate.Direction,
                    x.ResearchCandidate.Timeframe, x.ResearchCandidate.EvaluatedAtUtc, x.WindowEndUtc,
                    x.ResearchCandidate.Confidence, x.ResearchCandidate.Outcome, x.ResearchCandidate.MarketRegime,
                    x.ResearchCandidate.NormalizedLiquidity, x.GrossReturnBps!.Value, x.EstimatedCostBps!.Value,
                    x.NetReturnBps!.Value, x.MaximumFavorableExcursionBps!.Value, x.MaximumAdverseExcursionBps!.Value,
                    x.TargetStopOutcome, x.ResearchCandidate.FeatureVersion, x.ResearchCandidate.PatternVersion,
                    x.LabelVersion, x.InputSha256)).ToArray();
                var evaluation = await db.ResearchEvaluationRunRecords.SingleAsync(x => x.Id == evaluationId);
                evaluation.InputSha256 = ResearchEvaluationService.ComputeInputHash(hashRows,
                    new ResearchEvaluationRequest(60, StartUtc, StartUtc.AddDays(9), null,
                        "US-STK-SPY-SMART", "deterministic-patterns"), StartUtc, StartUtc.AddDays(9));
                await db.SaveChangesAsync();
                return evaluationId;
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        public sealed class SimpleFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
        {
            private int _createCount;
            public int CreateCount => Volatile.Read(ref _createCount);
            public TradingBotDbContext CreateDbContext()
            {
                Interlocked.Increment(ref _createCount);
                return new(options);
            }
            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        }
        private sealed class FixedClock(DateTime utcNow) : IClock { public DateTime UtcNow { get; } = utcNow; }
        private sealed class AdjustableClock(DateTime utcNow) : IClock { public DateTime UtcNow { get; set; } = utcNow; }
    }
}
