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
using DomainCandle = TradingBot.Domain.Models.Candle;

namespace TradingBot.Tests
{
    public sealed class DeterministicReplayServiceTests
    {
        private static readonly DateTime StartUtc = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task SameInputAndVersionsProduceSameSignalsAndOutputHashWithoutFutureData()
        {
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(5));
            var first = await harness.Service.StartAsync(Request());
            var second = await harness.Service.StartAsync(Request());

            first = await harness.Service.ProcessBatchAsync(first.Id, 100);
            second = await harness.Service.ProcessBatchAsync(second.Id, 100);
            var firstSignals = await harness.Service.GetSignalsAsync(first.Id);
            var secondSignals = await harness.Service.GetSignalsAsync(second.Id);

            Assert.Equal(ReplayRunStatus.Completed, first.Status);
            Assert.Equal(ReplayRunStatus.Completed, second.Status);
            Assert.Equal(first.InputSha256, second.InputSha256);
            Assert.Equal(first.OutputSha256, second.OutputSha256);
            Assert.Equal(firstSignals.Select(item => item.SignalId), secondSignals.Select(item => item.SignalId));
            Assert.Equal(5, firstSignals.Count);
            Assert.True(harness.Detectors.AllInputsWereOrderedAndAsOfCurrentEvent);
        }

        [Fact]
        public async Task PersistedCheckpointPausesAndResumesWithFreshServiceWithoutDuplicateSignals()
        {
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(5));
            var run = await harness.Service.StartAsync(Request());

            run = await harness.Service.ProcessBatchAsync(run.Id, 2);
            Assert.Equal(ReplayRunStatus.Running, run.Status);
            Assert.Equal(2, run.ProcessedEventCount);
            Assert.Equal("bar-2", run.CheckpointEventId);

            run = await harness.Service.PauseAsync(run.Id);
            var unchanged = await harness.Service.ProcessBatchAsync(run.Id, 100);
            Assert.Equal(ReplayRunStatus.Paused, unchanged.Status);
            Assert.Equal(2, unchanged.ProcessedEventCount);

            var restarted = harness.CreateFreshService();
            await restarted.ResumeAsync(run.Id, 0m);
            var completed = await restarted.ProcessBatchAsync(run.Id, 100);
            var signals = await restarted.GetSignalsAsync(run.Id);

            Assert.Equal(ReplayRunStatus.Completed, completed.Status);
            Assert.Equal(5, completed.ProcessedEventCount);
            Assert.Equal(5, completed.SignalCount);
            Assert.Equal(5, signals.Select(item => item.SignalId).Distinct().Count());
        }

        [Fact]
        public async Task ChangedDatasetAfterCheckpointFailsClosed()
        {
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(3));
            var run = await harness.Service.StartAsync(Request());
            run = await harness.Service.ProcessBatchAsync(run.Id, 1);
            harness.Dataset.Records.Add(Record(4));

            var result = await harness.Service.ProcessBatchAsync(run.Id, 100);

            Assert.Equal(ReplayRunStatus.Faulted, result.Status);
            Assert.Contains("input changed", result.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await harness.Service.GetSignalsAsync(run.Id));
        }

        [Fact]
        public async Task StartRejectsInvalidDatasetAndOutOfRangeSpeed()
        {
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(1));
            harness.Dataset.Verification = new DatasetVerificationResult(false, 1, 1, "bad", new[] { "tampered" });

            await Assert.ThrowsAsync<InvalidDataException>(() => harness.Service.StartAsync(Request()));

            harness.Dataset.Verification = new DatasetVerificationResult(true, 1, 1, "ok", Array.Empty<string>());
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => harness.Service.StartAsync(Request(10_001m)));
        }

        [Fact]
        public async Task MigrationCreatesReplayRunAndSignalTables()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options;
            await using (var db = new TradingBotDbContext(options))
            {
                await db.Database.MigrateAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ReplayRunRecords', 'ReplaySignalRecords');";
            Assert.Equal(2L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        private static ReplayStartRequest Request(decimal speed = 0m) => new(
            "US-STK-SPY-SMART",
            StartUtc,
            StartUtc.AddMinutes(10),
            speed,
            PipelineContractVersions.DefaultStrategyId);

        private static List<MarketDatasetRecord> CreateRecords(int count) =>
            Enumerable.Range(1, count).Select(Record).Reverse().ToList();

        private static MarketDatasetRecord Record(int minute) => new()
        {
            SchemaVersion = "market-data-v2.parquet-v1",
            EventId = $"bar-{minute}",
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            DataType = "bar",
            EventTimeUtc = StartUtc.AddMinutes(minute),
            ReceivedTimeUtc = StartUtc.AddMinutes(minute).AddMilliseconds(10),
            Source = "Test.Dataset",
            Sequence = minute,
            IsFinal = true,
            Timeframe = "1m",
            Open = 100m + minute,
            High = 101m + minute,
            Low = 99m + minute,
            Close = 100.5m + minute,
            Volume = 1_000m + minute,
            QualityStatus = MarketDataQualityStatus.Healthy.ToString(),
            CanPersist = true,
            CanTriggerTrading = true
        };

        private sealed class ReplayHarness : IAsyncDisposable
        {
            private readonly SqliteConnection _connection;
            private readonly ServiceProvider _provider;
            private readonly FixedClock _clock = new(StartUtc.AddHours(1));
            private readonly ReplaySettings _settings = new() { EventsPerBatch = 100, MaximumDelayMilliseconds = 0 };

            private ReplayHarness(
                SqliteConnection connection,
                ServiceProvider provider,
                FakeDatasetStore dataset,
                RecordingDetectorFactory detectors)
            {
                _connection = connection;
                _provider = provider;
                Dataset = dataset;
                Detectors = detectors;
                Service = CreateFreshService();
            }

            public FakeDatasetStore Dataset { get; }
            public RecordingDetectorFactory Detectors { get; }
            public DeterministicReplayService Service { get; }

            public static async Task<ReplayHarness> CreateAsync(List<MarketDatasetRecord> records)
            {
                var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                var services = new ServiceCollection();
                services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
                var provider = services.BuildServiceProvider();
                await using (var db = await provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>().CreateDbContextAsync())
                {
                    await db.Database.EnsureCreatedAsync();
                }
                return new ReplayHarness(connection, provider, new FakeDatasetStore(records), new RecordingDetectorFactory());
            }

            public DeterministicReplayService CreateFreshService() => new(
                Dataset,
                _provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>(),
                new FeatureEngine(),
                Detectors,
                _clock,
                Options.Create(_settings),
                NullLogger<DeterministicReplayService>.Instance);

            public async ValueTask DisposeAsync()
            {
                await _provider.DisposeAsync();
                await _connection.DisposeAsync();
            }
        }

        private sealed class FakeDatasetStore : IMarketDatasetStore
        {
            public FakeDatasetStore(List<MarketDatasetRecord> records)
            {
                Records = records;
                Verification = new DatasetVerificationResult(true, 1, records.Count, "valid", Array.Empty<string>());
            }

            public List<MarketDatasetRecord> Records { get; }
            public DatasetVerificationResult Verification { get; set; }
            public Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DatasetManifest());
            public Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Verification);
            public Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<MarketDatasetRecord>>(Records
                    .Where(item => item.InstrumentId == query.InstrumentId
                        && (!query.FromUtc.HasValue || item.EventTimeUtc >= query.FromUtc.Value)
                        && (!query.ToUtc.HasValue || item.EventTimeUtc < query.ToUtc.Value))
                    .ToArray());
        }

        private sealed class RecordingDetectorFactory : IPatternDetectorFactory
        {
            public bool AllInputsWereOrderedAndAsOfCurrentEvent { get; private set; } = true;
            public IPatternDetector Create() => new RecordingDetector(this);

            private sealed class RecordingDetector : IPatternDetector
            {
                private readonly RecordingDetectorFactory _owner;
                public RecordingDetector(RecordingDetectorFactory owner) => _owner = owner;

                public IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<DomainCandle> candles)
                {
                    var current = candles[^1];
                    _owner.AllInputsWereOrderedAndAsOfCurrentEvent &= candles
                        .Select(item => item.TimestampUtc)
                        .SequenceEqual(candles.Select(item => item.TimestampUtc).OrderBy(item => item));
                    _owner.AllInputsWereOrderedAndAsOfCurrentEvent &= candles.All(item => item.TimestampUtc <= current.TimestampUtc);
                    return new[]
                    {
                        new PatternCandidate(PatternType.Hammer, current.Symbol, current.Timeframe, current.TimestampUtc, 0.75m)
                    };
                }
            }
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
