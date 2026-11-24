using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexCollectionStreamAuditTests
{
    private static DateTime Start => ForexResearchFixture.Start;
    private static async Task<JsonElement> Audit(ForexResearchFixture f, int batch = 4096, ForexReplayCoverageEvidence? coverage = null, DateTime? from = null, DateTime? to = null) =>
        JsonSerializer.SerializeToElement(await ForexCollectionStreamAudit.AnalyzeAsync(f.Dataset, f.Trading, f.Aggregates, new(), "EURUSD",
            from ?? Start, to ?? Start.AddMinutes(4), coverage, batch));

    [Fact]
    public async Task StreamingMatchesLegacyAndBatchSizeDoesNotResetWarmup()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        await f.Dataset.WriteBatchAsync(rows);
        var old = ForexCollectionAudit.Analyze(rows, f.Trading, f.Aggregates, new(), "EURUSD", Start, Start.AddMinutes(4));
        var small = await Audit(f, 1); var large = await Audit(f, 17);
        Assert.Equal(JsonSerializer.Serialize(old.Intervals), small.GetProperty("Intervals").GetRawText());
        Assert.Equal(small.GetRawText(), large.GetRawText());
        Assert.True(small.GetProperty("Intervals")[0].GetProperty("PriceFeatureReadyFrames").GetInt32() > 0);
        Assert.False(small.GetProperty("CanUseAsExecutionDataset").GetBoolean());
    }

    [Fact]
    public async Task RejectedOrphanIsExplicitAndStillRejectedByTradeReplay()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records().ToList();
        var rejected = ForexResearchFixture.Records()[0]; rejected.EventId = "IBKR:FXQ:rejected:1:bid";
        rejected.CanPersist = false; rejected.QualityStatus = "Stale"; rejected.QualityReason = "Event is stale at receipt.";
        rejected.ReceivedTimeUtc = Start.AddSeconds(101); rows.Add(rejected);
        await f.Dataset.WriteBatchAsync(rows);
        var result = await Audit(f);
        Assert.Equal(1, result.GetProperty("RejectedDiagnosticPairs").GetInt64());
        Assert.Equal(1, result.GetProperty("RejectedDiagnosticEvents").GetInt64());
        Assert.Single(result.GetProperty("DiagnosticExamples").EnumerateArray());
        Assert.Equal(152, result.GetProperty("CompleteHealthyPairs").GetInt64());
        using var strict = new ForexReplayIndex();
        await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => strict.LoadAsync(f.Dataset, new("EURUSD",Start,Start.AddMinutes(4)), "EURUSD", f.Aggregates, default));
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("unknown-rejection")]
    [InlineData("conflict")]
    public async Task HealthyOrphanUnknownRejectionAndConflictingDuplicateFail(string fault)
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records().Skip(fault == "conflict" ? 0 : 1).ToList();
        if (fault == "unknown-rejection") { rows[0].CanPersist = false; rows[0].QualityStatus = "Unknown"; rows[0].QualityReason = "not trusted"; }
        await f.Dataset.WriteBatchAsync(rows);
        if (fault == "conflict")
        {
            // Separate overlapping file: the writer deduplicates IDs within one batch.
            var conflict = ForexResearchFixture.Records()[1]; conflict.Price += .1m;
            await f.Dataset.WriteBatchAsync([conflict]);
        }
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Audit(f));
        if (fault == "conflict") Assert.Contains("Conflicting duplicate", error.Message);
    }

    [Fact]
    public async Task RejectedMateCannotHideMalformedHealthySide()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        rows[0].Price = 0; rows[1].CanPersist = false; rows[1].QualityStatus = "Invalid"; rows[1].QualityReason = "Invalid price";
        await f.Dataset.WriteBatchAsync(rows);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Audit(f));
        Assert.Contains("Malformed healthy side", error.Message);
    }

    [Fact]
    public async Task CoverageResetsWarmupAndTrailingInterruptionSuppressesPendingBar()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var baseline = await Audit(f);
        var coverage = new ForexReplayCoverageEvidence("EURUSD", Start, Start.AddMinutes(4), "Test frozen incident",
            [new(Start.AddSeconds(151), Start.AddSeconds(159), "Disconnected"), new(Start.AddSeconds(189.5),Start.AddMinutes(4),"No reconnect")]);
        var result = await Audit(f, coverage: coverage);
        Assert.True(result.GetProperty("CoverageSuppressedPairs").GetInt64() > 0);
        Assert.True(result.GetProperty("Intervals")[0].GetProperty("PriceFeatureReadyFrames").GetInt32()
            < baseline.GetProperty("Intervals")[0].GetProperty("PriceFeatureReadyFrames").GetInt32());
        Assert.True(result.GetProperty("Intervals")[0].GetProperty("FinalBars").GetInt32()
            < baseline.GetProperty("Intervals")[0].GetProperty("FinalBars").GetInt32());
    }

    [Fact]
    public async Task CallbackSessionChangeResetsWarmupEvenWithoutCoverageFile()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        foreach (var row in rows.Where(x => x.EventTimeUtc >= Start.AddSeconds(100)))
            row.EventId = row.EventId.Replace("fx10", "reconnected");
        await f.Dataset.WriteBatchAsync(rows);
        var report = await Audit(f);
        Assert.Equal(1, report.GetProperty("CallbackSessionResets").GetInt64());
        Assert.True(report.GetProperty("Intervals")[0].GetProperty("MaximumConsecutiveFeatureBars").GetInt32() < 26);
    }

    [Fact]
    public async Task CrossesUtcMidnightWithoutLosingFeatureContinuity()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        var shift = TimeSpan.FromHours(11) + TimeSpan.FromMinutes(59);
        foreach (var row in rows) { row.EventTimeUtc += shift; row.ReceivedTimeUtc += shift; }
        await f.Dataset.WriteBatchAsync(rows);
        var result = await Audit(f, 7, from: Start + shift, to: Start + shift + TimeSpan.FromMinutes(4));
        Assert.True(result.GetProperty("Intervals")[0].GetProperty("MaximumConsecutiveFeatureBars").GetInt32() > 26);
        Assert.Equal(152, result.GetProperty("CompleteHealthyPairs").GetInt64());
    }

    [Fact]
    public async Task ClosedSessionDoesNotCountAsUsableObservationMinutes()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        foreach (var row in rows) { row.EventTimeUtc = row.EventTimeUtc.AddDays(4); row.ReceivedTimeUtc = row.ReceivedTimeUtc.AddDays(4); }
        await f.Dataset.WriteBatchAsync(rows);
        var report = await Audit(f, from: Start.AddDays(4), to: Start.AddDays(4).AddMinutes(4));
        Assert.Equal(0, report.GetProperty("UsableQuoteMinutes").GetInt32());
        Assert.Equal(0, report.GetProperty("Intervals")[0].GetProperty("FinalBars").GetInt32());
    }

    [Fact]
    public async Task MoreThanLegacyTwoHundredThousandEventsUseBoundedReader()
    {
        using var f = new ForexResearchFixture();
        var rows = Enumerable.Range(0,100001).SelectMany(i => new[] { "bid", "ask" }.Select(side => new MarketDatasetRecord
        {
            SchemaVersion="market-data-v1", EventId=$"IBKR:FXQ:capacity:{i:D6}:{side}", InstrumentId="EURUSD", Symbol="EURUSD", DataType=side,
            Source=ForexQuoteAggregator.QuoteSource, IsFinal=true, CanPersist=true, QualityStatus="Healthy", Price=side=="bid"?1.1m:1.10002m,
            EventTimeUtc=Start.AddMilliseconds(i*2), ReceivedTimeUtc=Start.AddMilliseconds(i*2+1)
        })).ToArray();
        await f.Dataset.WriteBatchAsync(rows);
        var report=await Audit(f);
        Assert.Equal(200002,report.GetProperty("EventCount").GetInt32());
        Assert.Equal(100001,report.GetProperty("CompleteHealthyPairs").GetInt64());
    }
}
