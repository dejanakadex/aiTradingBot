using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexCollectionAuditTests
{
    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ShortAggregateSettings Aggregates = new() { IntervalsSeconds = [5] };

    [Fact]
    public void DistinguishesEmptyFutureStaleAndIncompleteWindowsWithoutInventingBars()
    {
        var rows = Pair(1, 1.1).Concat(Pair(11, 10.9)).Concat(Pair(12, 12.1))
            .Concat(Pair(16, 19)).Concat(Pair(21, 21.1).Take(1)).Concat(Pair(26, 26.1)).ToArray();
        var report = Analyze(rows, 32); var interval = Assert.Single(report.Intervals);
        Assert.Equal(6, interval.ExpectedFinalWindows);
        Assert.Equal(1, interval.WindowsWithoutQuotes); Assert.Equal(3, interval.ObservedWindowsWithoutFinalBar);
        Assert.Equal(1, interval.MissingBarsWithFutureQuotes); Assert.Equal(1, interval.MissingBarsWithStaleQuotes);
        Assert.Equal(2, interval.FinalBars); Assert.Equal(1, interval.MaximumConsecutiveFeatureBars);
        Assert.Equal(2, report.FutureQuoteEvents); Assert.Equal(2, report.StaleQuoteEvents);
        Assert.Equal(1, report.IncompleteCallbackPairs); Assert.Equal(1, report.Diagnostics.IncompleteQuotes);
        Assert.Equal(0, interval.FramesWithAtLeast26Bars); Assert.Equal(-100d, report.MinimumReceiveMinusEventMilliseconds);
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(Analyze(rows.Reverse().ToArray(), 32)));
    }

    [Fact]
    public void RealFeatureEngineWarmsUpAndCallbackSessionRestartsContinuity()
    {
        var rows = Enumerable.Range(0, 30).SelectMany(i => Pair(i * 5 + 1, i * 5 + 1.1))
            .Concat(Pair(153, 153.1, "reconnected")).ToArray();
        var report = Analyze(rows, 157); var interval = Assert.Single(report.Intervals);
        Assert.Equal(2, report.CallbackSessions); Assert.Equal(31, interval.FinalBars);
        Assert.Equal(30, interval.MaximumConsecutiveFeatureBars); Assert.Equal(5, interval.FramesWithAtLeast26Bars);
        Assert.Equal(5, interval.PriceFeatureReadyFrames); Assert.Equal(2, interval.ConsecutiveBarHistogram[1]);
    }

    [Fact]
    public void RequestedCutoffAndSessionCalendarExcludeUnfinishedAndClosedWindows()
    {
        var rows = Pair(1, 1.1).Concat(Pair(6, 6.1)).Concat(Pair(11, 14)).ToArray();
        var report = Analyze(rows, 11.9); var interval = Assert.Single(report.Intervals);
        Assert.Equal(1, interval.ExpectedFinalWindows); Assert.Equal(1, interval.FinalBars);
        Assert.Equal(4, report.QuoteEvents); // A quote not yet received is not used as-of.
        var saturday = Start.AddDays(4);
        var closed = ForexCollectionAudit.Analyze([], ForexCollectionTests.Settings(), Aggregates, new(), "FX-EURUSD", saturday, saturday.AddHours(1));
        Assert.Equal(0, Assert.Single(closed.Intervals).ExpectedFinalWindows); Assert.Null(closed.FirstEventUtc);
        var empty = Analyze([], 17);
        Assert.Equal(3, Assert.Single(empty.Intervals).LongestEmptyRun);
    }

    [Theory]
    [InlineData("legacy")] [InlineData("identity")] [InlineData("duplicate")]
    public void IncompatibleInputCannotAppearAsSuccessfulReconstruction(string scenario)
    {
        var rows = Pair(1, 1.1).ToArray();
        if (scenario == "legacy") rows[0].Source = "IBKR.Legacy";
        if (scenario == "identity") rows[0].InstrumentId = "SPY";
        if (scenario == "duplicate") rows = [.. rows, rows[0]];
        Assert.Throws<InvalidDataException>(() => Analyze(rows, 7));
    }

    [Fact]
    public async Task CliNeedsNoDatabasePreservesDatasetAndRefusesEvidenceOverwriteOrTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "fx-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var dataset = Path.Combine(root, "datasets");
            var store = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = dataset }),
                new SystemClock(), NullLogger<ParquetMarketDatasetStore>.Instance);
            await store.WriteBatchAsync(Pair(1, 1.1).ToArray());
            var manifestBefore = (await store.VerifyAsync()).ManifestSha256;
            var config = Path.Combine(root, "config.json"); var output = Path.Combine(root, "report.json");
            await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { TradingSettings = ForexCollectionTests.Settings(), ShortAggregates = Aggregates }));
            string[] Args(string path) => ["fx-collection-audit", "--config", config, "--dataset", dataset,
                "--instrument-id", "FX-EURUSD", "--from", Start.ToString("O"), "--to", Start.AddSeconds(7).ToString("O"), "--output", path];
            Assert.Equal(0, await ModelOpsProgram.RunAsync(Args(output)));
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(output));
            Assert.False(report.RootElement.GetProperty("canSendOrders").GetBoolean());
            Assert.Equal(1, report.RootElement.GetProperty("audit").GetProperty("intervals")[0].GetProperty("finalBars").GetInt32());
            Assert.Equal(manifestBefore, (await store.VerifyAsync()).ManifestSha256);
            Assert.Empty(Directory.GetFiles(root, "*.db", SearchOption.AllDirectories));
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(output));
            Assert.Equal(1, await ModelOpsProgram.RunAsync(Args(output)));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(output)));
            await File.AppendAllTextAsync(Directory.GetFiles(dataset, "*.parquet", SearchOption.AllDirectories)[0], "tampered");
            var invalid = Path.Combine(root, "invalid.json");
            Assert.Equal(1, await ModelOpsProgram.RunAsync(Args(invalid))); Assert.False(File.Exists(invalid));
        }
        finally { Directory.Delete(root, true); }
    }

    private static ForexCollectionAuditReport Analyze(IReadOnlyList<MarketDatasetRecord> rows, double seconds) =>
        ForexCollectionAudit.Analyze(rows, ForexCollectionTests.Settings(), Aggregates, new(), "FX-EURUSD", Start, Start.AddSeconds(seconds));

    private static IEnumerable<MarketDatasetRecord> Pair(double time, double received, string session = "test") =>
        new[] { "bid", "ask" }.Select(side => new MarketDatasetRecord
        {
            SchemaVersion = TradingBot.Domain.Models.PipelineContractVersions.MarketData,
            EventId = $"IBKR:FXQ:{session}:{time}:{side}", InstrumentId = "FX-EURUSD", Symbol = "EURUSD", DataType = side,
            EventTimeUtc = Start.AddSeconds(time), ReceivedTimeUtc = Start.AddSeconds(received),
            Source = "IBKR.TickByTick.Forex.BidAsk.v2", Price = side == "bid" ? 1.1m + (decimal)time / 1000000m : 1.1002m + (decimal)time / 1000000m,
            IsFinal = true, CanPersist = true, QualityStatus = "Healthy"
        });
}
