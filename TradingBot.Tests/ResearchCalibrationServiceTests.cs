using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class ResearchCalibrationServiceTests
    {
        private static readonly DateTime StartUtc = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task GenerationIsIdempotentAndApprovalRequiresReviewedHash()
        {
            await using var harness = await Harness.CreateAsync();
            var evaluation = await harness.Evaluation.RunAsync(new ResearchEvaluationRequest(
                60, StartUtc, StartUtc.AddDays(30), InstrumentId: "US-STK-SPY-SMART"));

            var first = await harness.Calibration.GenerateAsync(new ResearchCalibrationRequest(evaluation.Id));
            var second = await harness.Calibration.GenerateAsync(new ResearchCalibrationRequest(evaluation.Id));

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(ResearchCalibrationStatus.Draft, first.Status);
            Assert.True(first.ThresholdStability.IsStable);
            Assert.True(first.ApprovalReady);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Calibration.DecideAsync(first.Id, new ResearchCalibrationDecisionRequest(
                ResearchCalibrationDecision.Approve, "reviewer", "Reviewed stable calibration.", "wrong-hash")));

            var approved = await harness.Calibration.DecideAsync(first.Id, new ResearchCalibrationDecisionRequest(
                ResearchCalibrationDecision.Approve, "reviewer", "Reviewed stable calibration.", first.OutputSha256));

            Assert.Equal(ResearchCalibrationStatus.Approved, approved.Status);
            Assert.Single(approved.ApprovalHistory);
            Assert.Equal("Approved", approved.ApprovalHistory[0].Action);
            Assert.False(approved.ApprovalReady);
        }

        [Fact]
        public async Task ApprovedProfileRanksOpportunitiesAndFailsClosedOnVersionMismatch()
        {
            await using var harness = await Harness.CreateAsync();
            var evaluation = await harness.Evaluation.RunAsync(new ResearchEvaluationRequest(
                60, StartUtc, StartUtc.AddDays(30), InstrumentId: "US-STK-SPY-SMART"));
            var draft = await harness.Calibration.GenerateAsync(new ResearchCalibrationRequest(evaluation.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Calibration.RankAsync(draft.Id, RankingRequest(evaluation)));
            var approved = await harness.Calibration.DecideAsync(draft.Id, new ResearchCalibrationDecisionRequest(
                ResearchCalibrationDecision.Approve, "reviewer", "Approve for deterministic ranking.", draft.OutputSha256));

            var ranking = await harness.Calibration.RankAsync(approved.Id, RankingRequest(evaluation));

            Assert.Equal(2, ranking.Opportunities.Count);
            Assert.Equal("high", ranking.Opportunities[0].CandidateKey);
            Assert.True(ranking.Opportunities[0].Eligible);
            Assert.False(ranking.Opportunities[1].Eligible);
            Assert.True(ranking.Opportunities[0].ExpectedNetReturnBps > ranking.Opportunities[1].ExpectedNetReturnBps);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Calibration.RankAsync(approved.Id,
                RankingRequest(evaluation) with { FeatureVersion = "features-other" }));
        }

        [Fact]
        public async Task NewApprovedModelSupersedesPreviousModelWithAuditHistory()
        {
            await using var harness = await Harness.CreateAsync();
            var evaluation = await harness.Evaluation.RunAsync(new ResearchEvaluationRequest(
                60, StartUtc, StartUtc.AddDays(30), InstrumentId: "US-STK-SPY-SMART"));
            var first = await harness.Calibration.GenerateAsync(new ResearchCalibrationRequest(evaluation.Id));
            await harness.Calibration.DecideAsync(first.Id, new ResearchCalibrationDecisionRequest(
                ResearchCalibrationDecision.Approve, "reviewer", "Approve first calibration model.", first.OutputSha256));
            var alternate = harness.CreateAlternateCalibrationService();
            var second = await alternate.GenerateAsync(new ResearchCalibrationRequest(evaluation.Id));

            await alternate.DecideAsync(second.Id, new ResearchCalibrationDecisionRequest(
                ResearchCalibrationDecision.Approve, "reviewer", "Approve replacement calibration model.", second.OutputSha256));
            var superseded = await harness.Calibration.GetAsync(first.Id);

            Assert.Equal(2, second.ModelVersion);
            Assert.NotNull(superseded);
            Assert.Equal(ResearchCalibrationStatus.Superseded, superseded.Status);
            Assert.Contains(superseded.ApprovalHistory, item => item.Action == "Superseded" && item.RelatedProfileId == second.Id);
        }

        [Fact]
        public async Task MigrationCreatesCalibrationAndApprovalTables()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            await using (var db = new TradingBotDbContext(options)) await db.Database.MigrateAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ResearchCalibrationProfileRecords', 'ResearchCalibrationApprovalRecords');";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        private static OpportunityRankingRequest RankingRequest(ResearchEvaluationRunSnapshot evaluation) => new(
            evaluation.FeatureVersion,
            evaluation.PatternVersion,
            evaluation.LabelVersion,
            new[]
            {
                new OpportunityRankingCandidate("low", "US-STK-SPY-SMART", "micro", 0.4m, 2m, StartUtc.AddDays(31)),
                new OpportunityRankingCandidate("high", "US-STK-SPY-SMART", "micro", 0.95m, 2m, StartUtc.AddDays(31))
            });

        private sealed class Harness : IAsyncDisposable
        {
            private readonly SqliteConnection _connection;
            private readonly ServiceProvider _provider;
            private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
            private readonly IClock _clock;

            private Harness(
                SqliteConnection connection,
                ServiceProvider provider,
                IDbContextFactory<TradingBotDbContext> dbFactory,
                IClock clock,
                IResearchEvaluationService evaluation,
                IResearchCalibrationService calibration)
            {
                _connection = connection;
                _provider = provider;
                _dbFactory = dbFactory;
                _clock = clock;
                Evaluation = evaluation;
                Calibration = calibration;
            }

            public IResearchEvaluationService Evaluation { get; }
            public IResearchCalibrationService Calibration { get; }

            public static async Task<Harness> CreateAsync()
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
                services.AddSingleton<IClock>(new FixedClock(StartUtc.AddDays(31)));
                services.AddSingleton<IResearchEvaluationService, ResearchEvaluationService>();
                services.AddSingleton<IResearchCalibrationService, ResearchCalibrationService>();
                services.Configure<ResearchEvaluationSettings>(settings =>
                {
                    settings.TrainingWindowDays = 10;
                    settings.ValidationWindowDays = 5;
                    settings.StepDays = 5;
                    settings.HoldoutDays = 5;
                    settings.MinimumTrainingSamples = 20;
                    settings.MinimumValidationSamples = 10;
                    settings.MinimumHoldoutSamples = 10;
                    settings.ConfidenceThresholds = new[] { 0m, 0.8m };
                    settings.CostStressMultipliers = new[] { 1m, 2m };
                });
                services.Configure<ResearchCalibrationSettings>(ConfigureCalibration);
                var provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
                await using (var db = await factory.CreateDbContextAsync())
                {
                    await db.Database.EnsureCreatedAsync();
                    await SeedAsync(db);
                }
                return new Harness(
                    connection,
                    provider,
                    factory,
                    provider.GetRequiredService<IClock>(),
                    provider.GetRequiredService<IResearchEvaluationService>(),
                    provider.GetRequiredService<IResearchCalibrationService>());
            }

            public IResearchCalibrationService CreateAlternateCalibrationService()
            {
                var settings = new ResearchCalibrationSettings();
                ConfigureCalibration(settings);
                settings.MinimumCalibrationBinSize = 6;
                return new ResearchCalibrationService(_dbFactory, Evaluation, _clock, Options.Create(settings));
            }

            private static void ConfigureCalibration(ResearchCalibrationSettings settings)
            {
                settings.MinimumCalibrationSamples = 20;
                settings.MinimumCalibrationFoldCount = 2;
                settings.MinimumCalibrationBinSize = 5;
                settings.CalibrationMetricBins = 5;
                settings.MaximumStableThresholdRange = 1m;
                settings.StableThresholdTolerance = 1m;
                settings.MinimumFoldAgreementRatio = 0.1m;
                settings.MaximumOutOfSampleBrierDegradation = 1m;
                settings.MaximumHoldoutExpectancyDegradationBps = 1_000m;
                settings.MaximumHoldoutDrawdownIncreaseBps = 10_000m;
                settings.MinimumHoldoutSampleRatio = 0.1m;
            }

            private static async Task SeedAsync(TradingBotDbContext db)
            {
                long id = 1;
                for (var day = 0; day < 30; day++)
                {
                    for (var sample = 0; sample < 4; sample++)
                    {
                        var highConfidence = sample < 2;
                        var net = highConfidence
                            ? ((day + sample) % 2 == 0 ? 10m : -5m)
                            : sample == 2 || day % 4 != 0 ? 1m : -20m;
                        var candidate = new ResearchCandidateRecord
                        {
                            RecordKey = $"LIVE|candidate-{id}", CandidateKey = $"candidate-{id}", SourceEventId = $"event-{id}",
                            InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", StrategyId = "micro",
                            PatternType = PatternType.Hammer, Direction = TradeDirection.Long, Timeframe = Timeframe.OneMinute,
                            EvaluatedAtUtc = StartUtc.AddDays(day).AddMinutes(sample), ReferencePrice = 100m,
                            Confidence = highConfidence ? 0.9m : 0.5m, Outcome = ResearchCandidateOutcome.Accepted,
                            DecisionStage = "PatternDetected", FeatureVersion = "features-test", PatternVersion = "patterns-test",
                            LabelVersion = "labels-test", MarketRegime = MarketRegime.Trending.ToString(), NormalizedLiquidity = 1.5m,
                            HardConditionsJson = "[]", ScoreComponentsJson = "[]", ReasonsJson = "[]", MetadataJson = "{}",
                            CreatedAtUtc = StartUtc, UpdatedAtUtc = StartUtc
                        };
                        candidate.Labels.Add(new CandidateLabelRecord
                        {
                            HorizonSeconds = 60, Status = CandidateLabelStatus.Complete,
                            TargetStopOutcome = net > 0m ? TargetStopOutcome.TargetFirst : TargetStopOutcome.StopFirst,
                            WindowStartUtc = candidate.EvaluatedAtUtc, WindowEndUtc = candidate.EvaluatedAtUtc.AddMinutes(1),
                            ObservationCount = 1, EntryPrice = 100m, ExitPrice = 100m,
                            MaximumFavorableExcursionBps = 12m, MaximumAdverseExcursionBps = 8m,
                            GrossReturnBps = net + 2m, EstimatedCostBps = 2m, NetReturnBps = net,
                            ReasonsJson = "[]", LabelVersion = "labels-test", CalculatedAtUtc = StartUtc.AddDays(31)
                        });
                        db.ResearchCandidateRecords.Add(candidate);
                        id++;
                    }
                }
                await db.SaveChangesAsync();
            }

            public async ValueTask DisposeAsync()
            {
                await _provider.DisposeAsync();
                await _connection.DisposeAsync();
            }
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
