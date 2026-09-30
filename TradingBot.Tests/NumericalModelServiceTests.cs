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
                "US-STK-SPY-SMART", "deterministic-patterns", "features-v2", 0.7m, (int)PatternType.BreakoutAndRetest,
                (int)TradeDirection.Long, (int)Timeframe.OneMinute, 2m, 0.2m, 2m, StartUtc.AddDays(10)));

            Assert.Equal(NumericalModelStatus.Approved, approved.Status);
            Assert.True(prediction.Eligible);
            Assert.True(prediction.Probability >= prediction.Threshold);
        }

        [Fact]
        public async Task PredictionWithoutApprovedModel_FailsClosed()
        {
            await using var harness = CreateHarness();
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.PredictAsync(new NumericalModelPredictionRequest(
                "US-STK-SPY-SMART", "deterministic-patterns", "features-v2", 0.7m, 1, 1, 1, 1m, 1m, 2m, StartUtc)));
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
            private SimpleFactory Factory { get; }
            public NumericalModelService Service { get; }

            public async Task<Guid> SeedEvaluationAndLabelsAsync()
            {
                var evaluationId = Guid.NewGuid();
                var fold = new WalkForwardFoldResult(1, StartUtc, StartUtc.AddDays(4), StartUtc.AddDays(4), StartUtc.AddDays(6), 80, 40, 0.5m, "test", null, null);
                await using var db = Factory.CreateDbContext();
                db.ResearchEvaluationRunRecords.Add(new ResearchEvaluationRunRecord
                {
                    Id = evaluationId, RunKey = Guid.NewGuid().ToString("N"), InstrumentId = "US-STK-SPY-SMART", StrategyId = "deterministic-patterns",
                    HorizonSeconds = 60, FromUtc = StartUtc, ToUtc = StartUtc.AddDays(9), HoldoutStartUtc = StartUtc.AddDays(7),
                    SourceCandidateCount = 160, EligibleCandidateCount = 160, WalkForwardFoldCount = 1, SelectedConfidenceThreshold = 0.5m,
                    SelectionReason = "test", EvaluationVersion = "evaluation-v1", FeatureVersion = "features-v2", PatternVersion = "patterns-v2", LabelVersion = "labels-v1",
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
                        Outcome = ResearchCandidateOutcome.Accepted, DecisionStage = "test", FeatureVersion = "features-v2", PatternVersion = "patterns-v2",
                        LabelVersion = "labels-v1", MarketRegime = "Trending", NormalizedLiquidity = winning ? 2m : 0.2m, NormalizedVolatility = 0.2m,
                        HardConditionsJson = "[]", ScoreComponentsJson = "[]", ReasonsJson = "[]", MetadataJson = "{}", CreatedAtUtc = times[i], UpdatedAtUtc = times[i]
                    };
                    candidate.Labels.Add(new CandidateLabelRecord
                    {
                        HorizonSeconds = 60, Status = CandidateLabelStatus.Complete, TargetStopOutcome = winning ? TargetStopOutcome.TargetFirst : TargetStopOutcome.StopFirst,
                        WindowStartUtc = times[i], WindowEndUtc = times[i].AddMinutes(1), ObservationCount = 10, EntryPrice = 100m, ExitPrice = winning ? 100.1m : 99.9m,
                        MaximumFavorableExcursionBps = winning ? 10m : 2m, MaximumAdverseExcursionBps = winning ? 2m : 10m,
                        GrossReturnBps = winning ? 12m : -8m, EstimatedCostBps = 2m, NetReturnBps = winning ? 10m : -10m,
                        ReasonsJson = "[]", LabelVersion = "labels-v1", CalculatedAtUtc = times[i].AddMinutes(1)
                    });
                    db.ResearchCandidateRecords.Add(candidate);
                }
                await db.SaveChangesAsync();
                return evaluationId;
            }

            public ValueTask DisposeAsync() => Connection.DisposeAsync();
        }

        private sealed class SimpleFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
        {
            public TradingBotDbContext CreateDbContext() => new(options);
            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new(CreateDbContext());
        }
        private sealed class FixedClock(DateTime utcNow) : IClock { public DateTime UtcNow { get; } = utcNow; }
    }
}
