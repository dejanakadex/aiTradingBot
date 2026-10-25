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

        [Fact]
        public async Task ForexModelUsesPriceOnlyForecastExactContractApprovalAndRollback()
        {
            await using var h = CreateHarness(); using var f = new ForexResearchFixture(); f.Profile.HoldingSeconds = 60;
            var evaluationId = await SeedForexAsync(h, f);
            var service = ForexModel(h, f);
            var draft = await service.TrainAsync(new(evaluationId));
            Assert.Equal(ForexResearchContract.Model, draft.ModelVersionTag);
            Assert.True(draft.ApprovalReady, string.Join(" ", draft.ApprovalReasons));
            Assert.True(draft.CandidateHoldout.ForecastMeanAbsoluteErrorBps < draft.BaselineHoldout.ForecastMeanAbsoluteErrorBps);
            var c = f.Contract().Get("EURUSD", "patterns");
            var request = new NumericalModelPredictionRequest("EURUSD", "patterns", c.Feature, .7m, (int)PatternType.BreakoutAndRetest,
                (int)TradeDirection.Long, (int)Timeframe.FiveSeconds, 0, .002m, 2, StartUtc.AddDays(10))
                { PatternVersion = c.Pattern, ExecutionLabelVersion = c.Label };
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PredictAsync(request));
            await service.DecideAsync(draft.Id, new(true, "test", "Synthetic FX holdout reviewed.", draft.OutputSha256));
            var predictions = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => service.PredictAsync(request)));
            Assert.All(predictions, x => { Assert.True(x.Eligible); Assert.True(x.ExpectedGrossReturnBps > 5); });
            Assert.False((await service.PredictAsync(request with { EstimatedCostBps = 1000 })).Eligible);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PredictAsync(request with { PatternVersion = "stock" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PredictAsync(request with { NormalizedLiquidity = 1 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PredictAsync(request with { StrategyId = "other" }));
            var clone = await h.CloneAsDraftAsync(draft.Id);
            await service.DecideAsync(clone.Id, new(true, "test", "Replacement FX model reviewed.", clone.OutputSha256));
            Assert.Equal(clone.Id, (await service.PredictAsync(request)).ModelId);
            await service.RollbackAsync(draft.Id, new("test", "Restore reviewed FX artifact.", draft.OutputSha256));
            Assert.Equal(draft.Id, (await service.PredictAsync(request)).ModelId);
            Assert.Equal(3, (await service.GetDecisionHistoryAsync(draft.Id)).Count);
        }

        [Fact]
        public async Task ForexHoldoutChangesCannotChangeFittedModelOrProbabilityCutoff()
        {
            await using var a = CreateHarness(); await using var b = CreateHarness(); using var f = new ForexResearchFixture(); f.Profile.HoldingSeconds = 60;
            var firstId = await SeedForexAsync(a, f); var secondId = await SeedForexAsync(b, f, changeHoldout: true);
            var first = await ForexModel(a, f).TrainAsync(new(firstId)); var second = await ForexModel(b, f).TrainAsync(new(secondId));
            Assert.Equal(first.ProbabilityThreshold, second.ProbabilityThreshold);
            Assert.Equal(first.CandidateWalkForward, second.CandidateWalkForward);
            Assert.NotEqual(first.CandidateHoldout, second.CandidateHoldout);
            await using var da = a.Factory.CreateDbContext(); await using var db = b.Factory.CreateDbContext();
            var artifactA = (await da.NumericalModelRecords.SingleAsync()).ModelArtifact;
            var artifactB = (await db.NumericalModelRecords.SingleAsync()).ModelArtifact;
            // ML.NET ZIP timestamps may differ; compare the actual model entries, not container timestamps.
            using var za = new System.IO.Compression.ZipArchive(new MemoryStream(artifactA));
            using var zb = new System.IO.Compression.ZipArchive(new MemoryStream(artifactB));
            foreach (var entry in za.Entries.Where(x => x.Name == "Model.key"))
            {
                using var sa = entry.Open(); using var sb = zb.GetEntry(entry.FullName)!.Open();
                using var ma = new MemoryStream(); using var mb = new MemoryStream(); sa.CopyTo(ma); sb.CopyTo(mb);
                Assert.Equal(ma.ToArray(), mb.ToArray());
            }
        }

        private static NumericalModelService ForexModel(Harness h, ForexResearchFixture f) => new(h.Factory,
            Options.Create(new NumericalModelSettings { RandomSeed = 42, NumberOfLeaves = 4, NumberOfIterations = 30, LearningRate = .1,
                MinimumExampleCountPerLeaf = 2, MinimumTrainingSamples = 40, MinimumValidationSelections = 5, MinimumHoldoutSelections = 5,
                MinimumWalkForwardExpectancyImprovementBps = .1m, MinimumHoldoutExpectancyImprovementBps = 0, ProbabilityThresholds = [.5m, .6m, .7m] }),
            new FixedClock(StartUtc.AddDays(10)), forexContract: f.Contract());

        [Fact]
        public async Task ForexEvaluationUsesPurgedTemporalWorkflowAndRejectsChangedReplay()
        {
            await using var h = CreateHarness(); using var f = new ForexResearchFixture(); f.Profile.HoldingSeconds = 60;
            await SeedForexAsync(h, f);
            await using var db = h.Factory.CreateDbContext(); var replay = await db.ReplayRunRecords.SingleAsync();
            var evaluation = new ResearchEvaluationService(h.Factory, new FixedClock(StartUtc.AddDays(10)),
                Options.Create(new ResearchEvaluationSettings { TrainingWindowDays = 4, ValidationWindowDays = 1, StepDays = 1, HoldoutDays = 2,
                    MinimumTrainingSamples = 40, MinimumValidationSamples = 5, MinimumHoldoutSamples = 5, ConfidenceThresholds = [0m, .5m] }));
            var request = new ResearchEvaluationRequest(60, StartUtc, StartUtc.AddDays(9), replay.Id, "EURUSD", "patterns");
            var result = await evaluation.RunAsync(request);
            var model = await ForexModel(h, f).TrainAsync(new(result.Id));
            Assert.Equal(ForexResearchContract.Model, model.ModelVersionTag);
            Assert.Equal(result.Id, (await evaluation.RunAsync(request)).Id);
            var label = await db.CandidateLabelRecords.FirstAsync(); label.GrossReturnBps += 1; await db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidDataException>(() => evaluation.RunAsync(request));
            await Assert.ThrowsAsync<InvalidDataException>(() => ForexModel(h, f).TrainAsync(new(result.Id)));
        }

        private static async Task<Guid> SeedForexAsync(Harness h, ForexResearchFixture f, bool changeHoldout = false)
        {
            var id = await h.SeedEvaluationAndLabelsAsync(); var c = f.Contract().Get("EURUSD", "patterns");
            await using var db = h.Factory.CreateDbContext();
            var replay = new ReplayRunRecord { Id = Guid.NewGuid(), Status = ReplayRunStatus.Completed, InstrumentId = "EURUSD", StrategyId = "patterns", StrategyVersion = c.Label };
            db.ReplayRunRecords.Add(replay);
            var labels = await db.CandidateLabelRecords.Include(x => x.ResearchCandidate).ToArrayAsync();
            foreach (var l in labels)
            {
                var x = l.ResearchCandidate; x.ReplayRunId = replay.Id; x.InstrumentId = "EURUSD"; x.Symbol = "EURUSD"; x.StrategyId = "patterns";
                x.FeatureVersion = c.Feature; x.PatternVersion = c.Pattern; x.LabelVersion = l.LabelVersion = c.Label;
                x.Timeframe = Timeframe.FiveSeconds; x.NormalizedVolatility = x.NormalizedLiquidity == 2 ? .002m : .0002m; x.NormalizedLiquidity = null;
                if (changeHoldout && x.EvaluatedAtUtc >= StartUtc.AddDays(7)) { l.GrossReturnBps *= -2; l.NetReturnBps = l.GrossReturnBps - l.EstimatedCostBps; }
            }
            await db.SaveChangesAsync(); replay.OutputSha256 = await DeterministicReplayService.ComputeOutputHashAsync(db, replay.Id, default);
            var e = await db.ResearchEvaluationRunRecords.SingleAsync(x => x.Id == id);
            e.ReplayRunId = replay.Id; e.InstrumentId = "EURUSD"; e.StrategyId = "patterns"; e.FeatureVersion = c.Feature; e.PatternVersion = c.Pattern; e.LabelVersion = c.Label;
            var rows = labels.Select(x => new ResearchEvaluationObservation(x.ResearchCandidateId, x.ResearchCandidate.CandidateKey,
                x.ResearchCandidate.InstrumentId, x.ResearchCandidate.StrategyId, x.ResearchCandidate.PatternType, x.ResearchCandidate.Direction,
                x.ResearchCandidate.Timeframe, x.ResearchCandidate.EvaluatedAtUtc, x.WindowEndUtc, x.ResearchCandidate.Confidence,
                x.ResearchCandidate.Outcome, x.ResearchCandidate.MarketRegime, x.ResearchCandidate.NormalizedLiquidity,
                x.GrossReturnBps!.Value, x.EstimatedCostBps!.Value, x.NetReturnBps!.Value, x.MaximumFavorableExcursionBps!.Value,
                x.MaximumAdverseExcursionBps!.Value, x.TargetStopOutcome, x.ResearchCandidate.FeatureVersion, x.ResearchCandidate.PatternVersion, x.LabelVersion, x.InputSha256)).ToArray();
            e.InputSha256 = ResearchEvaluationService.ComputeInputHash(rows, new(60, StartUtc, StartUtc.AddDays(9), replay.Id, "EURUSD", "patterns"), StartUtc, StartUtc.AddDays(9));
            await db.SaveChangesAsync(); return id;
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
