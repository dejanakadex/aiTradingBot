using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using static TradingBot.Tests.ForexResearchFixture;

namespace TradingBot.Tests;

public sealed class ForexStreamingReplayTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    public async Task BatchBoundariesDoNotResetWarmupPositionOrEquity(int batchSize)
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        var baseline = await f.Replay().RunForexResearchAsync(Request());
        var segmented = await f.Replay().RunForexResearchAsync(Request() with { ForexBatchSize = batchSize });
        Assert.True(baseline.ClosedTradeCount > 0);
        Assert.Equal(baseline.InputSha256, segmented.InputSha256);
        Assert.Equal(baseline.OutputSha256, segmented.OutputSha256);
        Assert.Equal(baseline.Trades, segmented.Trades);
        Assert.InRange(segmented.ForexDataset!.MaximumYieldedBatchEvents, 1, batchSize);
        Assert.Equal(Records().Length, segmented.ForexDataset.UniqueEvents);
        Assert.NotEmpty(Directory.GetFiles(f.DatabasePath + ".fx-manifests", "*.json"));
    }

    [Fact]
    public async Task MoreThanTwoHundredThousandEventsUsesBoundedRowGroupsAndIndex()
    {
        using var f = new ForexResearchFixture();
        var rows = new List<MarketDatasetRecord>();
        for (var i = 0; i < 100001; i++)
            foreach (var side in new[] { "bid", "ask" }) rows.Add(new()
            {
                SchemaVersion = "market-data-v1", EventId = $"IBKR:FXQ:large:{i:D8}:{side}", InstrumentId = "EURUSD", Symbol = "EURUSD",
                DataType = side, EventTimeUtc = Start.AddTicks(i * 1000L), ReceivedTimeUtc = Start.AddTicks(i * 1000L + 100),
                Source = ForexQuoteAggregator.QuoteSource, IsFinal = true, CanPersist = true, QualityStatus = "Healthy",
                Price = side == "bid" ? 1.1m : 1.10002m
            });
        await f.Dataset.WriteBatchAsync(rows); rows.Clear();
        var snapshot = Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, "large-snapshot");
        await ForexDatasetSnapshot.ExportAsync(f.DatasetPath, snapshot, "EURUSD", Start, Start.AddDays(8));
        f.Dataset = new(Options.Create(new DatasetStorageSettings { RootPath = snapshot }), f, NullLogger<ParquetMarketDatasetStore>.Instance);
        var result = await f.Replay().RunForexResearchAsync(new("EURUSD", Start, Start.AddDays(8), StrategyId: "patterns") { ForexBatchSize = 2048 });
        Assert.Equal(200002, result.ForexDataset!.UniqueEvents);
        Assert.InRange(result.ForexDataset.MaximumYieldedBatchEvents, 1, 2048);
        Assert.Equal(65536, result.ForexDataset.MaximumParquetRowGroupRows);
        Assert.Equal(0, result.ClosedTradeCount); // Ten seconds cannot satisfy 26 five-second bars.
    }

    [Fact]
    public async Task IdenticalOverlapIsCountedOnceAndConflictCannotSilentlyOverride()
    {
        using var f = new ForexResearchFixture(); var rows = Records(); await f.Dataset.WriteBatchAsync(rows);
        var baseline = await f.Replay().RunForexResearchAsync(Request());
        await f.Dataset.WriteBatchAsync(rows.Take(12).ToArray()); // Separate file with overlapping IDs.
        var duplicate = await f.Replay().RunForexResearchAsync(Request());
        Assert.Equal(12, duplicate.ForexDataset!.DuplicateEvents);
        Assert.Equal(rows.Length, duplicate.ForexDataset.UniqueEvents);
        Assert.Equal(baseline.Trades, duplicate.Trades);
        Assert.NotEqual(baseline.InputSha256, duplicate.InputSha256); // New source manifest is explicitly versioned.
        rows[0].Price -= .00001m;
        await f.Dataset.WriteBatchAsync([rows[0]]);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Replay().RunForexResearchAsync(Request()));
        await using var db = f.CreateDbContext();
        Assert.Equal(2, await db.ReplayRunRecords.CountAsync()); // Conflict rejected before creating an attempt.
    }

    [Fact]
    public async Task InterruptedAttemptIsPreservedAndRebuiltResultEqualsUninterruptedRun()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        f.SaveInterceptor = new FailCheckpointOnce();
        await Assert.ThrowsAsync<IOException>(() => f.Replay().RunForexResearchAsync(Request() with { ForexBatchSize = 13 }));
        Guid failedId; int partialCount;
        await using (var db = f.CreateDbContext())
        {
            var failed = await db.ReplayRunRecords.SingleAsync(); failedId = failed.Id;
            Assert.Equal(ReplayRunStatus.Faulted, failed.Status);
            partialCount = await db.ResearchCandidateRecords.CountAsync(); Assert.True(partialCount > 0);
        }
        var recovered = await f.Replay().RunForexResearchAsync(Request() with { ForexResumeRunId = failedId, ForexBatchSize = 7 });
        var fresh = await f.Replay().RunForexResearchAsync(Request());
        Assert.NotEqual(failedId, recovered.ReplayRunId);
        Assert.Equal(failedId, recovered.ForexDataset!.RebuiltFromRunId);
        Assert.Equal("DeterministicRebuildInNewAttempt", recovered.ForexDataset.RecoveryMode);
        Assert.Equal(fresh.OutputSha256, recovered.OutputSha256);
        await using var verify = f.CreateDbContext();
        Assert.Equal(partialCount, await verify.ResearchCandidateRecords.CountAsync(x => x.ReplayRunId == failedId));
        Assert.Equal(ReplayRunStatus.Faulted, (await verify.ReplayRunRecords.FindAsync(failedId))!.Status);
        Assert.Empty(await verify.OrderRecords.ToArrayAsync());
    }

    [Theory]
    [InlineData("contract")]
    [InlineData("range")]
    [InlineData("manifest")]
    public async Task RecoveryRejectsChangedInputOrPolicy(string changed)
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        f.SaveInterceptor = new FailCheckpointOnce();
        await Assert.ThrowsAsync<IOException>(() => f.Replay().RunForexResearchAsync(Request() with { ForexBatchSize = 13 }));
        await using var db = f.CreateDbContext(); var prior = await db.ReplayRunRecords.SingleAsync();
        var request = Request() with { ForexResumeRunId = prior.Id };
        if (changed == "contract") f.Profile.HoldingSeconds++;
        if (changed == "range") request = request with { ToUtc = request.ToUtc.AddSeconds(1) };
        if (changed == "manifest") await f.Dataset.WriteBatchAsync(Records().Take(2).ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Replay().RunForexResearchAsync(request));
        Assert.Equal(1, await db.ReplayRunRecords.CountAsync());
    }

    [Fact]
    public async Task MultiDayRunFeedsTemporalEvaluationWithoutCombiningIndependentPortfolios()
    {
        using var f = new ForexResearchFixture();
        var originalRules = f.Profile.HistoricalRules[0];
        f.Profile.HistoricalRules = Enumerable.Range(0, 4).Select(day => originalRules with {
            ObservedUtc = Start.AddDays(day), Rules = originalRules.Rules with { VerifiedAtUtc = Start.AddDays(day) } }).ToArray();
        for (var day = 0; day < 4; day++)
        {
            var rows = Records();
            foreach (var row in rows)
            {
                row.EventId = row.EventId.Replace(":fx10:", $":day{day}:", StringComparison.Ordinal);
                row.EventTimeUtc = row.EventTimeUtc.AddDays(day); row.ReceivedTimeUtc = row.ReceivedTimeUtc.AddDays(day);
            }
            await f.Dataset.WriteBatchAsync(rows);
        }
        var from = Start.Date; var to = Start.AddDays(4);
        var run = await f.Replay().RunForexResearchAsync(new("EURUSD", from, to, StrategyId: "patterns") { ForexBatchSize = 11 });
        Assert.True(run.Trades.Where(x => x.Status == "Closed").Select(x => x.SignalTimeUtc.Date).Distinct().Count() > 1);
        var evaluation = new ResearchEvaluationService(f, f, Options.Create(new ResearchEvaluationSettings {
            TrainingWindowDays = 2, ValidationWindowDays = 1, StepDays = 1, HoldoutDays = 1,
            MinimumTrainingSamples = 1, MinimumValidationSamples = 1, MinimumHoldoutSamples = 1 })); // Synthetic test only.
        var result = await evaluation.RunAsync(new(10, from, to, run.ReplayRunId, "EURUSD", "patterns"));
        Assert.True(result.WalkForwardFoldCount > 0);
        Assert.True(result.HoldoutMetrics.SampleCount > 0);
        Assert.All(result.Folds, fold => Assert.True(fold.ValidationToUtc <= result.HoldoutStartUtc));
        await using var db = f.CreateDbContext();
        var saved = await db.ResearchEvaluationRunRecords.SingleAsync();
        Assert.Equal(run.ReplayRunId, saved.ReplayRunId);
        Assert.StartsWith(ForexResearchContract.Labels, saved.LabelVersion);
        Assert.Empty(await db.OrderRecords.ToArrayAsync());
    }

    private static ReplayStartRequest Request() => new("EURUSD", Start, Start.AddSeconds(192), StrategyId: "patterns");
    private sealed class FailCheckpointOnce : SaveChangesInterceptor
    {
        private bool _armed = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_armed && eventData.Context!.ChangeTracker.Entries<ReplayRunRecord>().Any(x => x.Entity.ProcessedEventCount >= 280))
            { _armed = false; throw new IOException("Injected checkpoint interruption."); }
            return new(result);
        }
    }
}
