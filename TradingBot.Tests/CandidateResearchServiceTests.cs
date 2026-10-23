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
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class CandidateResearchServiceTests
    {
        private static readonly DateTime StartUtc = new(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task PersistsEveryOutcomeIdempotentlyAndCalculatesAllMatureLabels()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

            var settings = new CandidateLabelingSettings
            {
                HorizonsSeconds = new[] { 5 },
                DataAvailabilityGraceSeconds = 0,
                TargetMoveBps = 12m,
                StopMoveBps = 8m
            };
            var dataset = new FakeDatasetStore(new[]
            {
                Price("trade-1", StartUtc.AddSeconds(1), 100.05m),
                Price("trade-2", StartUtc.AddSeconds(5), 100.15m)
            });
            var service = new CandidateResearchService(dataset, factory, new FixedClock(StartUtc.AddMinutes(1)),
                Options.Create(settings), NullLogger<CandidateResearchService>.Instance);
            var candidate = new PatternCandidate(
                PatternType.Hammer, "SPY", Timeframe.OneMinute, StartUtc, 0.75m,
                new[] { 100m },
                new Dictionary<string, string> { ["referencePrice"] = "100" },
                PipelineContext.CreateForSignal("US-STK-SPY-SMART", "LONG|HAMMER|1M", "micro", "features-test", "patterns-test"));
            var accepted = PatternEvaluation.FromCandidate(candidate);
            var rejected = new PatternEvaluation
            {
                PatternType = PatternType.BullishEngulfing,
                Direction = TradeDirection.Long,
                InstrumentId = candidate.InstrumentId,
                Symbol = candidate.Symbol,
                StrategyId = candidate.StrategyId,
                FeatureVersion = candidate.Context.FeatureVersion,
                PatternVersion = candidate.PatternVersion,
                Timeframe = candidate.Timeframe,
                EvaluatedAtUtc = StartUtc,
                ReferencePrice = 100m,
                Reasons = new[] { "Hard condition failed." }
            };
            var batch = new PatternDetectionBatch
            {
                Evaluations = new[] { accepted, rejected },
                Candidates = new[] { candidate }
            };

            Assert.Equal(2, await service.PersistBatchAsync(batch, "bar-1"));
            Assert.Equal(0, await service.PersistBatchAsync(batch, "bar-1"));
            Assert.Equal(2, await service.LabelMatureCandidatesAsync(StartUtc.AddSeconds(5)));
            await service.MarkBlockedAsync(candidate.PatternKey, "Risk", new[] { "Risk rejected." });

            var records = await service.GetCandidatesAsync();
            Assert.Equal(2, records.Count);
            Assert.Contains(records, item => item.Outcome == ResearchCandidateOutcome.Rejected);
            var blocked = Assert.Single(records, item => item.Outcome == ResearchCandidateOutcome.Blocked);
            Assert.Equal("Risk", blocked.DecisionStage);
            var labels = await service.GetLabelsAsync(blocked.Id);
            var label = Assert.Single(labels);
            Assert.Equal(CandidateLabelStatus.Complete, label.Status);
            Assert.Equal(TargetStopOutcome.TargetFirst, label.TargetStopOutcome);
            Assert.True(label.NetReturnBps < label.GrossReturnBps);
            Assert.NotEmpty(label.InputSha256);
            Assert.Equal(64, label.InputSha256.Length);
            Assert.NotNull(label.HorizonGrossReturnBps);
        }

        [Fact]
        public async Task ShortCandidateLabelStartsAtAvailabilityAndExcludesEarlierTicks()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
            await using var provider = services.BuildServiceProvider();
            var factory = provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();
            await using (var db = await factory.CreateDbContextAsync()) await db.Database.EnsureCreatedAsync();

            var market = new[]
            {
                Price("before-availability", StartUtc.AddSeconds(1), 200m),
                Price("after-availability", StartUtc.AddSeconds(11), 100.05m),
                Price("horizon", StartUtc.AddSeconds(15), 100.10m)
            };
            var service = new CandidateResearchService(new FakeDatasetStore(market), factory,
                new FixedClock(StartUtc.AddMinutes(1)),
                Options.Create(new CandidateLabelingSettings { HorizonsSeconds = [5],
                    DataAvailabilityGraceSeconds = 0 }),
                NullLogger<CandidateResearchService>.Instance);
            var candidate = new PatternCandidate(PatternType.Hammer, "SPY", Timeframe.FiveSeconds,
                StartUtc, 0.75m, metadata: new Dictionary<string, string> { ["referencePrice"] = "100" },
                context: PipelineContext.CreateForSignal("US-STK-SPY-SMART", "LONG|HAMMER|5S"));
            var batch = new PatternDetectionBatch
            {
                Evaluations = [PatternEvaluation.FromCandidate(candidate)], Candidates = [candidate]
            };
            var quoteTime = StartUtc.AddSeconds(9.9);
            var availability = StartUtc.AddSeconds(10);
            var context = new CandidateEvaluationContext(MarketRegime.Unknown, null, null,
                availability, 100m, quoteTime, 100.02m, quoteTime);

            Assert.Equal(1, await service.PersistBatchAsync(batch, "source-tick", evaluationContext: context));
            var stored = Assert.Single(await service.GetCandidatesAsync());
            Assert.Equal(availability, stored.EvaluatedAtUtc);
            var pending = Assert.Single(await service.GetLabelsAsync(stored.Id));
            Assert.Equal(availability, pending.WindowStartUtc);
            Assert.Equal(StartUtc.AddSeconds(15), pending.WindowEndUtc);
            Assert.Equal(100.01m, pending.EntryPrice);

            Assert.Equal(1, await service.LabelMatureCandidatesAsync(StartUtc.AddSeconds(15)));
            var complete = Assert.Single(await service.GetLabelsAsync(stored.Id));
            Assert.Equal(CandidateLabelStatus.Complete, complete.Status);
            Assert.Equal(DeterministicReplayService.ComputeInputHash(market.Skip(1).ToArray()), complete.InputSha256);

            var laterDetection = StartUtc.AddSeconds(20);
            var staleCandidate = new PatternCandidate(PatternType.Hammer, "SPY", Timeframe.FiveSeconds,
                laterDetection, 0.75m, metadata: new Dictionary<string, string> { ["referencePrice"] = "100" },
                context: PipelineContext.CreateForSignal("US-STK-SPY-SMART", "LONG|HAMMER|5S|STALE"));
            var staleBatch = new PatternDetectionBatch
            {
                Evaluations = [PatternEvaluation.FromCandidate(staleCandidate)], Candidates = [staleCandidate]
            };
            var staleContext = new CandidateEvaluationContext(MarketRegime.Unknown, null, null,
                StartUtc.AddSeconds(30), 100m, StartUtc.AddSeconds(28), 100.02m, StartUtc.AddSeconds(28));
            Assert.Equal(1, await service.PersistBatchAsync(staleBatch, "stale-tick", evaluationContext: staleContext));
            var staleRecord = Assert.Single(await service.GetCandidatesAsync(), item => item.SignalId == staleCandidate.Context.SignalId);
            var insufficient = Assert.Single(await service.GetLabelsAsync(staleRecord.Id));
            Assert.Equal(CandidateLabelStatus.InsufficientData, insufficient.Status);
            Assert.Null(insufficient.NetReturnBps);
        }

        [Fact]
        public async Task MigrationCreatesCandidateAndLabelTables()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            await using (var db = new TradingBotDbContext(options)) await db.Database.MigrateAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ResearchCandidateRecords', 'CandidateLabelRecords');";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        private static MarketDatasetRecord Price(string eventId, DateTime time, decimal price) => new()
        {
            EventId = eventId,
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            DataType = "trade",
            EventTimeUtc = time,
            ReceivedTimeUtc = time,
            Price = price,
            IsFinal = true,
            CanPersist = true,
            QualityStatus = MarketDataQualityStatus.Healthy.ToString()
        };

        private sealed class FakeDatasetStore : IMarketDatasetStore
        {
            private readonly IReadOnlyList<MarketDatasetRecord> _records;
            public FakeDatasetStore(IReadOnlyList<MarketDatasetRecord> records) => _records = records;
            public Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<MarketDatasetRecord>>(_records.Where(item =>
                    (!query.FromUtc.HasValue || item.EventTimeUtc >= query.FromUtc.Value)
                    && (!query.ToUtc.HasValue || item.EventTimeUtc < query.ToUtc.Value)).ToArray());
            public Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DatasetManifest());
            public Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DatasetVerificationResult(true, 0, 0, string.Empty, Array.Empty<string>()));
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
