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
    public sealed class ResearchEvaluationServiceTests
    {
        private static readonly DateTime StartUtc = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task RunIsPersistedAndIdempotentForSameInputAndConfiguration()
        {
            await using var harness = await Harness.CreateAsync();
            var request = new ResearchEvaluationRequest(60, StartUtc, StartUtc.AddDays(30), InstrumentId: "US-STK-SPY-SMART");

            var first = await harness.Service.RunAsync(request);
            var second = await harness.Service.RunAsync(request);
            var fetched = await harness.Service.GetAsync(first.Id);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.InputSha256, second.InputSha256);
            Assert.Equal(first.OutputSha256, second.OutputSha256);
            Assert.NotNull(fetched);
            Assert.Equal(0.8m, first.SelectedConfidenceThreshold);
            Assert.True(first.WalkForwardFoldCount > 0);
            Assert.NotEmpty(first.Segments);
        }

        [Fact]
        public async Task MigrationCreatesEvaluationTableAndCandidateContextColumns()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            await using (var db = new TradingBotDbContext(options)) await db.Database.MigrateAsync();

            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ResearchEvaluationRunRecords';";
            Assert.Equal(1L, Convert.ToInt64(await tableCommand.ExecuteScalarAsync()));
            await using var columnCommand = connection.CreateCommand();
            columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ResearchCandidateRecords') WHERE name IN ('MarketRegime', 'NormalizedLiquidity', 'NormalizedVolatility');";
            Assert.Equal(3L, Convert.ToInt64(await columnCommand.ExecuteScalarAsync()));
        }

        private sealed class Harness : IAsyncDisposable
        {
            private readonly SqliteConnection _connection;
            private readonly ServiceProvider _provider;
            private Harness(SqliteConnection connection, ServiceProvider provider, IResearchEvaluationService service)
            {
                _connection = connection;
                _provider = provider;
                Service = service;
            }

            public IResearchEvaluationService Service { get; }

            public static async Task<Harness> CreateAsync()
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
                services.AddSingleton<IClock>(new FixedClock(StartUtc.AddDays(31)));
                services.AddSingleton<IResearchEvaluationService, ResearchEvaluationService>();
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
                var provider = services.BuildServiceProvider();
                var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
                await using (var db = await factory.CreateDbContextAsync())
                {
                    await db.Database.EnsureCreatedAsync();
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
                return new Harness(connection, provider, provider.GetRequiredService<IResearchEvaluationService>());
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
