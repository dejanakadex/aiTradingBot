using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
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
        public async Task ChangedPatternConfigurationAfterCheckpointFailsClosed()
        {
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(3));
            var run = await harness.Service.StartAsync(Request());
            run = await harness.Service.ProcessBatchAsync(run.Id, 1);
            harness.Detectors.DetectorVersion = "patterns-v1+config-changed";

            var result = await harness.Service.ProcessBatchAsync(run.Id, 100);

            Assert.Equal(ReplayRunStatus.Faulted, result.Status);
            Assert.Contains("configuration changed", result.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await harness.Service.GetSignalsAsync(run.Id));
        }

        [Fact]
        public async Task ChangedFeatureConfigurationAfterCheckpointFailsClosed()
        {
            var featureEngine = new VersionedFeatureEngine();
            await using var harness = await ReplayHarness.CreateAsync(CreateRecords(3), featureEngine);
            var run = await harness.Service.StartAsync(Request());
            run = await harness.Service.ProcessBatchAsync(run.Id, 1);
            featureEngine.Version = "features-v2+config-changed";

            var result = await harness.Service.ProcessBatchAsync(run.Id, 100);

            Assert.Equal(ReplayRunStatus.Faulted, result.Status);
            Assert.Contains("feature configuration changed", result.LastError, StringComparison.OrdinalIgnoreCase);
            Assert.Single(await harness.Service.GetSignalsAsync(run.Id));
        }

        [Fact]
        public async Task ReplayUsesAsOfBidAskEventsInCanonicalFeatureOutput()
        {
            var records = CreateRecords(1);
            records.Add(Quote("bid-1", "bid", StartUtc.AddSeconds(30), 100.98m));
            records.Add(Quote("ask-1", "ask", StartUtc.AddSeconds(40), 101.02m));
            await using var harness = await ReplayHarness.CreateAsync(records);
            var run = await harness.Service.StartAsync(Request());

            run = await harness.Service.ProcessBatchAsync(run.Id, 100);
            var signal = Assert.Single(await harness.Service.GetSignalsAsync(run.Id));
            using var features = JsonDocument.Parse(signal.FeaturesJson);

            Assert.Equal(ReplayRunStatus.Completed, run.Status);
            Assert.Equal(3, run.InputEventCount);
            Assert.Equal(0.04m, features.RootElement.GetProperty("spread").GetDecimal());
            Assert.True(features.RootElement.GetProperty("spreadBps").GetDecimal() > 0m);
            Assert.Equal(20_000, features.RootElement.GetProperty("quoteAgeMilliseconds").GetInt64());
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
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ReplayRunRecords', 'ReplaySignalRecords', 'ReplayBacktestRecords');";
            Assert.Equal(3L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        [Fact]
        public async Task CompletedReplayBacktestIsPersistedAndIdempotent()
        {
            var records = CreateRecords(1);
            records.Add(Quote("bid-before", "bid", StartUtc.AddSeconds(59), 100m));
            records.Add(Quote("ask-before", "ask", StartUtc.AddSeconds(59), 100.02m));
            var askFill = Quote("ask-fill", "ask", StartUtc.AddSeconds(61), 100.02m);
            askFill.Size = 40m;
            records.Add(askFill);
            var bidExit = Quote("bid-exit", "bid", StartUtc.AddSeconds(65), 100.60m);
            bidExit.Size = 40m;
            records.Add(bidExit);
            records.Add(Quote("bid-horizon", "bid", StartUtc.AddSeconds(122), 100.70m));
            await using var harness = await ReplayHarness.CreateAsync(records);
            var run = await harness.Service.StartAsync(Request());
            run = await harness.Service.ProcessBatchAsync(run.Id, 100);
            Assert.Equal(ReplayRunStatus.Completed, run.Status);

            var backtest = harness.CreateBacktestService();
            var first = await backtest.RunAsync(run.Id, new ReplayBacktestRequest());
            var second = await backtest.RunAsync(run.Id, new ReplayBacktestRequest());
            var stored = await backtest.GetAsync(first.Id);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(first.OutputSha256, stored!.OutputSha256);
            Assert.Equal(1, first.FilledTradeCount);
            Assert.Equal(1, first.ClosedTradeCount);
            Assert.Equal("Target", Assert.Single(first.Trades).ExitReason);
            Assert.NotNull(first.Trades[0].HorizonReturnBps);

            var changedPolicy = await harness.CreateBacktestService(new ScalpingExecutionSettings
            {
                SafetyBufferBps = 4m
            }).RunAsync(run.Id, new ReplayBacktestRequest());
            Assert.NotEqual(first.Id, changedPolicy.Id);
            Assert.NotEqual(first.RequestSha256, changedPolicy.RequestSha256);

            harness.Dataset.Records.Add(Quote("tamper", "bid", StartUtc.AddMinutes(3), 100.50m));
            await Assert.ThrowsAsync<InvalidDataException>(() => backtest.RunAsync(run.Id, new ReplayBacktestRequest()));
        }

        [Fact]
        public async Task TickOnlyShortReplayUsesLiveTriggerRulesAndBacktestsAfterBarAvailability()
        {
            var trading = new TradingSettings { Instruments =
            [
                new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                    StrategyIds = [PipelineContractVersions.DefaultStrategyId] }
            ] };
            var shortSignals = new ShortSignalSettings { Profiles =
            [
                new ShortSignalProfile { InstrumentId = "US-STK-SPY-SMART",
                    StrategyId = PipelineContractVersions.DefaultStrategyId, IntervalSeconds = 5,
                    PatternTypes = [PatternType.Hammer], MinimumBars = 2,
                    MinimumConfidence = 0.5m, CooldownSeconds = 0, MaximumSignalAgeSeconds = 15 }
            ] };
            var records = new List<MarketDatasetRecord>
            {
                Quote("trade-0", "trade", StartUtc.AddMilliseconds(250), 100m),
                Quote("trade-5", "trade", StartUtc.AddSeconds(5.25), 100.01m),
                Quote("bid-decision", "bid", StartUtc.AddSeconds(11.9), 100m),
                Quote("ask-decision", "ask", StartUtc.AddSeconds(11.9), 100.02m),
                Quote("trade-close", "trade", StartUtc.AddSeconds(12), 100.01m),
                Quote("ask-fill", "ask", StartUtc.AddSeconds(13), 100.02m),
                Quote("bid-target", "bid", StartUtc.AddSeconds(14), 100.60m)
            };
            records[5].Size = 40m;
            records[6].Size = 40m;
            await using var harness = await ReplayHarness.CreateAsync(records, trading: trading,
                shortSignals: shortSignals, withResearch: true);
            var run = await harness.Service.StartAsync(Request() with { ToUtc = StartUtc.AddSeconds(15) });
            run = await harness.Service.ProcessBatchAsync(run.Id, 4);
            Assert.Empty(await harness.Service.GetSignalsAsync(run.Id));

            var resumed = harness.CreateFreshService();
            run = await resumed.ProcessBatchAsync(run.Id, 100);
            var signal = Assert.Single(await resumed.GetSignalsAsync(run.Id));
            Assert.True(run.Status == ReplayRunStatus.Completed, run.LastError);
            Assert.Equal(Timeframe.FiveSeconds, signal.Timeframe);
            Assert.Equal(StartUtc.AddSeconds(10), signal.DetectedAtUtc);
            Assert.Equal("bid-decision", signal.SourceEventId);
            using (var metadata = JsonDocument.Parse(signal.MetadataJson))
                Assert.Equal(StartUtc.AddSeconds(12), metadata.RootElement.GetProperty("replayAvailableAtUtc").GetDateTime());
            await using (var researchDb = await harness.Factory.CreateDbContextAsync())
            {
                var candidates = await researchDb.ResearchCandidateRecords.AsNoTracking()
                    .Where(candidate => candidate.ReplayRunId == run.Id).ToArrayAsync();
                Assert.NotEmpty(candidates);
                Assert.Contains(candidates, candidate => candidate.SignalId == signal.SignalId
                    && candidate.Outcome == ResearchCandidateOutcome.Accepted);
                var linked = Assert.Single(candidates, candidate => candidate.SignalId == signal.SignalId);
                Assert.Equal(StartUtc.AddSeconds(12), linked.EvaluatedAtUtc);
                var labels = await researchDb.CandidateLabelRecords.AsNoTracking()
                    .Where(label => label.ResearchCandidateId == linked.Id).ToArrayAsync();
                Assert.All(labels, label =>
                {
                    Assert.Equal(StartUtc.AddSeconds(12), label.WindowStartUtc);
                    Assert.Equal(100.01m, label.EntryPrice);
                });
            }
            var backtest = await harness.CreateBacktestService().RunAsync(run.Id, new ReplayBacktestRequest());
            var trade = Assert.Single(backtest.Trades);
            Assert.Equal("Closed", trade.Status);
            Assert.Equal("Target", trade.ExitReason);
            Assert.Equal(StartUtc.AddSeconds(13), trade.EntryTimeUtc);
            Assert.Equal(StartUtc.AddSeconds(14), trade.ExitTimeUtc);
        }

        [Fact]
        public async Task ShortReplayRefusesResumeAfterProfileChanges()
        {
            var trading = new TradingSettings { Instruments =
            [
                new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                    StrategyIds = [PipelineContractVersions.DefaultStrategyId] }
            ] };
            var profile = new ShortSignalProfile { InstrumentId = "US-STK-SPY-SMART",
                StrategyId = PipelineContractVersions.DefaultStrategyId, IntervalSeconds = 5,
                PatternTypes = [PatternType.Hammer], MinimumBars = 2 };
            var shortSignals = new ShortSignalSettings { Profiles = [profile] };
            await using var harness = await ReplayHarness.CreateAsync(
                [Quote("trade-0", "trade", StartUtc.AddSeconds(1), 100m),
                    Quote("trade-1", "trade", StartUtc.AddSeconds(6), 101m)],
                trading: trading, shortSignals: shortSignals);
            var run = await harness.Service.StartAsync(Request());
            run = await harness.Service.ProcessBatchAsync(run.Id, 1);
            Assert.Equal(ReplayRunStatus.Running, run.Status);

            profile.MinimumBars = 3;
            run = await harness.CreateFreshService().ProcessBatchAsync(run.Id, 100);
            Assert.Equal(ReplayRunStatus.Faulted, run.Status);
            Assert.Contains("configuration changed", run.LastError, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ShortReplayKeepsFilteredResearchCandidateWithoutEmittingSignal()
        {
            var trading = new TradingSettings { Instruments =
            [
                new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                    StrategyIds = [PipelineContractVersions.DefaultStrategyId] }
            ] };
            var shortSignals = new ShortSignalSettings { Profiles =
            [
                new ShortSignalProfile { InstrumentId = "US-STK-SPY-SMART",
                    StrategyId = PipelineContractVersions.DefaultStrategyId, IntervalSeconds = 5,
                    PatternTypes = [PatternType.Hammer], MinimumBars = 2,
                    MinimumConfidence = 0.9m, MaximumSignalAgeSeconds = 15 }
            ] };
            await using var harness = await ReplayHarness.CreateAsync(
                [Quote("trade-0", "trade", StartUtc.AddMilliseconds(250), 100m),
                    Quote("trade-5", "trade", StartUtc.AddSeconds(5.25), 100.01m)],
                trading: trading, shortSignals: shortSignals, withResearch: true);
            var run = await harness.Service.StartAsync(Request() with { ToUtc = StartUtc.AddSeconds(15) });

            run = await harness.Service.ProcessBatchAsync(run.Id, 100);

            Assert.True(run.Status == ReplayRunStatus.Completed, run.LastError);
            Assert.Empty(await harness.Service.GetSignalsAsync(run.Id));
            await using var db = await harness.Factory.CreateDbContextAsync();
            Assert.Contains(await db.ResearchCandidateRecords.AsNoTracking()
                    .Where(candidate => candidate.ReplayRunId == run.Id).ToArrayAsync(),
                candidate => candidate.Outcome == ResearchCandidateOutcome.Blocked);
        }

        [Fact]
        public async Task VirtualTimerClosesShortBarWithoutAnotherTickButDoesNotInventFill()
        {
            var trading = new TradingSettings { Instruments =
            [
                new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                    StrategyIds = [PipelineContractVersions.DefaultStrategyId] }
            ] };
            var shortSignals = new ShortSignalSettings { Profiles =
            [
                new ShortSignalProfile { InstrumentId = "US-STK-SPY-SMART",
                    StrategyId = PipelineContractVersions.DefaultStrategyId, IntervalSeconds = 5,
                    PatternTypes = [PatternType.Hammer], MinimumBars = 2,
                    MinimumConfidence = 0.5m, CooldownSeconds = 0, MaximumSignalAgeSeconds = 15 }
            ] };
            await using var harness = await ReplayHarness.CreateAsync(
                [Quote("trade-0", "trade", StartUtc.AddMilliseconds(250), 100m),
                    Quote("trade-5", "trade", StartUtc.AddSeconds(5.25), 100.01m)],
                trading: trading, shortSignals: shortSignals, withResearch: true);
            var request = Request() with { ToUtc = StartUtc.AddSeconds(15) };
            var first = await harness.Service.StartAsync(request);
            first = await harness.Service.ProcessBatchAsync(first.Id, 100);
            var signal = Assert.Single(await harness.Service.GetSignalsAsync(first.Id));
            Assert.Equal(ReplayRunStatus.Completed, first.Status);
            Assert.Equal("trade-5", signal.SourceEventId);
            using (var metadata = JsonDocument.Parse(signal.MetadataJson))
                Assert.Equal(StartUtc.AddSeconds(12), metadata.RootElement.GetProperty("replayAvailableAtUtc").GetDateTime());
            await using (var researchDb = await harness.Factory.CreateDbContextAsync())
            {
                var labels = await researchDb.CandidateLabelRecords.AsNoTracking()
                    .Where(label => label.ResearchCandidate.ReplayRunId == first.Id).ToArrayAsync();
                Assert.NotEmpty(labels);
                Assert.All(labels, label =>
                {
                    Assert.Equal(CandidateLabelStatus.InsufficientData, label.Status);
                    Assert.Null(label.NetReturnBps);
                });
            }

            var backtest = await harness.CreateBacktestService().RunAsync(first.Id, new ReplayBacktestRequest());
            Assert.Equal("NoExecutableQuote", Assert.Single(backtest.Trades).Status);
            Assert.Equal(0, backtest.FilledTradeCount);

            var second = await harness.Service.StartAsync(request);
            second = await harness.Service.ProcessBatchAsync(second.Id, 1);
            second = await harness.CreateFreshService().ProcessBatchAsync(second.Id, 100);
            Assert.Equal(first.OutputSha256, second.OutputSha256);
            Assert.Equal(signal.SignalId, Assert.Single(await harness.Service.GetSignalsAsync(second.Id)).SignalId);
        }

        [Fact]
        public async Task ShortReplayExcludesTradeReceivedAfterItsWindowClosed()
        {
            var trading = new TradingSettings { Instruments =
            [
                new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY",
                    StrategyIds = [PipelineContractVersions.DefaultStrategyId] }
            ] };
            var shortSignals = new ShortSignalSettings { Profiles =
            [
                new ShortSignalProfile { InstrumentId = "US-STK-SPY-SMART",
                    StrategyId = PipelineContractVersions.DefaultStrategyId, IntervalSeconds = 5,
                    PatternTypes = [PatternType.Hammer], MinimumBars = 2,
                    MinimumConfidence = 0.5m, CooldownSeconds = 0, MaximumSignalAgeSeconds = 15 }
            ] };
            var late = Quote("trade-late", "trade", StartUtc.AddSeconds(5.25), 100.01m);
            late.ReceivedTimeUtc = StartUtc.AddSeconds(13);
            await using var harness = await ReplayHarness.CreateAsync(
                [Quote("trade-0", "trade", StartUtc.AddMilliseconds(250), 100m), late],
                trading: trading, shortSignals: shortSignals);
            var run = await harness.Service.StartAsync(Request() with { ToUtc = StartUtc.AddSeconds(15) });
            run = await harness.Service.ProcessBatchAsync(run.Id, 100);
            Assert.Equal(ReplayRunStatus.Completed, run.Status);
            Assert.Empty(await harness.Service.GetSignalsAsync(run.Id));

            harness.Dataset.Records[0].ReceivedTimeUtc = StartUtc.AddSeconds(20);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                harness.Service.StartAsync(Request() with { ToUtc = StartUtc.AddSeconds(25) }));
            Assert.Contains("receive-time order", error.Message, StringComparison.OrdinalIgnoreCase);
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

        private static MarketDatasetRecord Quote(string eventId, string dataType, DateTime eventTimeUtc, decimal price) => new()
        {
            SchemaVersion = "market-data-v2.parquet-v1",
            EventId = eventId,
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            DataType = dataType,
            EventTimeUtc = eventTimeUtc,
            ReceivedTimeUtc = eventTimeUtc.AddMilliseconds(10),
            Source = "Test.Dataset",
            IsFinal = true,
            Price = price,
            Size = 1m,
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
            private readonly IFeatureEngine _featureEngine;
            private readonly TradingSettings _trading;
            private readonly ShortSignalSettings _shortSignals;
            private readonly bool _withResearch;

            private ReplayHarness(
                SqliteConnection connection,
                ServiceProvider provider,
                FakeDatasetStore dataset,
                RecordingDetectorFactory detectors,
                IFeatureEngine featureEngine, TradingSettings trading, ShortSignalSettings shortSignals,
                bool withResearch)
            {
                _connection = connection;
                _provider = provider;
                Dataset = dataset;
                Detectors = detectors;
                _featureEngine = featureEngine;
                _trading = trading;
                _shortSignals = shortSignals;
                _withResearch = withResearch;
                Service = CreateFreshService();
            }

            public FakeDatasetStore Dataset { get; }
            public RecordingDetectorFactory Detectors { get; }
            public DeterministicReplayService Service { get; }
            public IDbContextFactory<TradingBotDbContext> Factory =>
                _provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>();

            public static async Task<ReplayHarness> CreateAsync(List<MarketDatasetRecord> records, IFeatureEngine? featureEngine = null,
                TradingSettings? trading = null, ShortSignalSettings? shortSignals = null, bool withResearch = false)
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
                return new ReplayHarness(
                    connection,
                    provider,
                    new FakeDatasetStore(records),
                    new RecordingDetectorFactory(),
                    featureEngine ?? new FeatureEngine(), trading ?? new TradingSettings(),
                    shortSignals ?? new ShortSignalSettings(), withResearch);
            }

            public DeterministicReplayService CreateFreshService() => new(
                Dataset,
                _provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>(),
                _featureEngine,
                Detectors,
                _clock,
                Options.Create(_settings),
                NullLogger<DeterministicReplayService>.Instance,
                candidateResearchService: _withResearch ? new CandidateResearchService(
                    Dataset, Factory, _clock, Options.Create(new CandidateLabelingSettings()),
                    NullLogger<CandidateResearchService>.Instance) : null,
                tradingSettings: Options.Create(_trading), shortSignalSettings: Options.Create(_shortSignals));

            public ReplayBacktestService CreateBacktestService(ScalpingExecutionSettings? execution = null) => new(
                _provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>(),
                Dataset,
                new ReplayTradeSimulator(
                    new PositionSizer(Options.Create(new RiskSettings())),
                    Options.Create(new RiskSettings()),
                    Options.Create(execution ?? new ScalpingExecutionSettings())),
                _clock);

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
            public string DetectorVersion { get; set; } = "patterns-v1+config-test";
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
                        new PatternCandidate(PatternType.Hammer, current.Symbol, current.Timeframe,
                            current.TimestampUtc, 0.75m,
                            metadata: new Dictionary<string, string>
                            {
                                ["referencePrice"] = current.Close.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            },
                            context: PipelineContext.CreateForSignal(current.InstrumentId,
                                $"Long|Hammer|{current.Timeframe}|{current.TimestampUtc:O}",
                                patternVersion: _owner.DetectorVersion))
                    };
                }
            }
        }

        private sealed class VersionedFeatureEngine : IFeatureEngine
        {
            private readonly FeatureEngine _inner = new();
            public string Version { get; set; } = "features-v2+config-test";
            public string FeatureVersion => Version;
            public MarketFeatures ComputeFeatures(IReadOnlyList<DomainCandle> candles) => _inner.ComputeFeatures(candles);
            public MarketFeatures ComputeFeatures(CanonicalFeatureInput input) => _inner.ComputeFeatures(input);
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
