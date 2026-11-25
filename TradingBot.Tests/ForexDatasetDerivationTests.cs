using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexDatasetDerivationTests
{
    private static DateTime Start => ForexResearchFixture.Start;
    private static ForexReplayCoverageEvidence Coverage => new("EURUSD", Start, Start.AddMinutes(4), "Frozen test incidents", []);
    private static string Output(ForexResearchFixture f, string name = "derived") => Path.Combine(Path.GetDirectoryName(f.DatabasePath)!, name);
    private static ParquetMarketDatasetStore Store(ForexResearchFixture f, string path) => new(Options.Create(new DatasetStorageSettings { RootPath = path }), f, NullLogger<ParquetMarketDatasetStore>.Instance);
    private static Task<ForexDerivationProof> Derive(ForexResearchFixture f, string name = "derived", int batch = 17) =>
        ForexDatasetDerivation.DeriveAsync(f.DatasetPath, Output(f, name), f.Trading, f.Aggregates, "EURUSD", Start, Start.AddMinutes(4),
            Coverage, new string('A', 64), new string('B', 64), batch);
    private static MarketDatasetRecord Rejected(double eventSecond = 140, double receiveSecond = 151.05)
    {
        var row = ForexResearchFixture.Records()[0]; row.EventId = "IBKR:FXQ:fx10:rejected:bid";
        row.EventTimeUtc = Start.AddSeconds(eventSecond); row.ReceivedTimeUtc = Start.AddSeconds(receiveSecond);
        row.QualityStatus = "Stale"; row.CanPersist = false; row.QualityReason = "Event is stale at receipt.";
        return row;
    }

    [Fact]
    public async Task CompleteLedgerAndConservativeCoveragePreserveRawEvidenceAndExactRetainedContent()
    {
        using var f = new ForexResearchFixture(); var rejected = Rejected();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records().Append(rejected).ToArray());
        var before = await f.Dataset.VerifyAsync();
        var proof = await Derive(f);
        Assert.Equal(305, proof.SourceEvents); Assert.Equal(304, proof.RetainedEvents); Assert.Equal(1, proof.ExcludedEvents);
        Assert.True(proof.StrictReplayInputVerified); Assert.False(proof.N5Complete); Assert.False(proof.CanSendOrders);
        Assert.Equal(before.ManifestSha256, (await f.Dataset.VerifyAsync()).ManifestSha256);
        var ledger = File.ReadAllLines(Path.Combine(Output(f), "exclusions.jsonl")); Assert.Single(ledger);
        var persisted = (await f.Dataset.ReadAsync(new("EURUSD", Start, Start.AddMinutes(4), ["bid", "ask"]))).Single(x => x.EventId == rejected.EventId);
        Assert.Equal(JsonSerializer.Serialize(persisted), JsonDocument.Parse(ledger[0]).RootElement.GetProperty("Record").GetRawText());
        var output = Store(f, Output(f)); Assert.True((await output.VerifyAsync()).IsValid);
        using var strict = new ForexReplayIndex();
        await strict.LoadAsync(output, new("EURUSD", Start, Start.AddMinutes(4)), "EURUSD", f.Aggregates, default);
        Assert.Equal(proof.RetainedCanonicalSha256, strict.CanonicalSha256.ToUpperInvariant());
        var coverage = (await output.GetBoundForexCoverageAsync())!;
        Assert.Contains(coverage.Interruptions, x => x.StartUtc == Start.AddSeconds(140) && x.EndUtc == Start.AddSeconds(151.1));
        Assert.Empty(proof.OriginalCoverage.Interruptions);
    }

    [Fact]
    public async Task BatchSizeChangesNeitherLedgerCoverageNorRetainedCanonicalEvents()
    {
        using var f = new ForexResearchFixture();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records().Append(Rejected()).ToArray());
        var one = await Derive(f, "one", 1); var many = await Derive(f, "many", 32);
        Assert.Equal(one.RetainedCanonicalSha256, many.RetainedCanonicalSha256);
        Assert.Equal(one.LedgerSha256, many.LedgerSha256); Assert.Equal(one.CoverageSha256, many.CoverageSha256);
        Assert.Equal(one.SourceEvents, one.RetainedEvents + one.ExcludedEvents);
    }

    [Fact]
    public async Task TrailingDiagnosticRetainsUnknownCoverageToTheCutoff()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records().Append(Rejected(190, 230)).ToArray());
        await Derive(f); var coverage = (await Store(f, Output(f)).GetBoundForexCoverageAsync())!;
        Assert.Contains(coverage.Interruptions, x => x.EndUtc == Start.AddMinutes(4) && x.StartUtc == Start.AddSeconds(189.1));
    }

    [Fact]
    public async Task HealthyButOldQuotesAreNotRemovedToMakeReplayLookBetter()
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records();
        foreach (var row in rows) row.ReceivedTimeUtc = row.ReceivedTimeUtc.AddSeconds(3);
        await f.Dataset.WriteBatchAsync(rows); var proof = await Derive(f);
        Assert.Equal(304, proof.RetainedEvents); Assert.Equal(0, proof.ExcludedEvents);
        Assert.Empty(File.ReadAllText(Path.Combine(Output(f), "exclusions.jsonl")));
    }

    [Theory]
    [InlineData("orphan")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task UnaccountedSourceDefectsCannotPublishCompletedOutput(string fault)
    {
        using var f = new ForexResearchFixture(); var rows = ForexResearchFixture.Records().ToList();
        if (fault == "orphan") rows.RemoveAt(0);
        if (fault == "unknown") { rows[0].CanPersist = false; rows[0].QualityStatus = "Unknown"; rows[0].QualityReason = "untrusted"; }
        await f.Dataset.WriteBatchAsync(rows);
        if (fault == "duplicate") await f.Dataset.WriteBatchAsync(rows.Take(2).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => Derive(f));
        Assert.False(Directory.Exists(Output(f)));
    }

    [Theory]
    [InlineData("coverage.json")]
    [InlineData("exclusions.jsonl")]
    [InlineData("derivation.json")]
    [InlineData("metadata")]
    public async Task MissingOrChangedEvidenceFailsBeforeReplayCreatesAnAttempt(string file)
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records()); await Derive(f);
        if (file == "metadata")
        {
            var path = Path.Combine(Output(f), "derivation.json");
            var content = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            content["configSha256"] = new string('C', 64);
            await File.WriteAllTextAsync(path, content.ToJsonString());
        }
        else if (file == "derivation.json") File.Delete(Path.Combine(Output(f), file));
        else File.AppendAllText(Path.Combine(Output(f), file), " ");
        f.Dataset = Store(f, Output(f));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Replay().RunForexResearchAsync(new("EURUSD", Start, Start.AddMinutes(4), StrategyId: "patterns")));
        using var db = f.CreateDbContext(); Assert.Empty(db.ReplayRunRecords); Assert.Empty(db.OrderRecords);
    }

    [Fact]
    public async Task ReplayAutomaticallyUsesCoverageAndCannotReplaceItWithAnEmptyOverride()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records().Append(Rejected()).ToArray()); await Derive(f);
        f.Dataset = Store(f, Output(f)); var bound = await f.Dataset.GetBoundForexCoverageAsync();
        var request = new ReplayStartRequest("EURUSD", Start, Start.AddMinutes(4), StrategyId: "patterns");
        var automatic = await f.Replay().RunForexResearchAsync(request);
        var explicitCoverage = await f.Replay().RunForexResearchAsync(request with { ForexCoverage = bound });
        Assert.Equal(automatic.InputSha256, explicitCoverage.InputSha256); Assert.Equal(automatic.OutputSha256, explicitCoverage.OutputSha256);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Replay().RunForexResearchAsync(request with { ForexCoverage = Coverage }));
        using var db = f.CreateDbContext(); Assert.Equal(2, db.ReplayRunRecords.Count()); Assert.Empty(db.OrderRecords);
    }

    [Fact]
    public async Task CompletedDerivationCannotBeAppendedOverwrittenOrSnapshottedWithoutItsBinding()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records()); await Derive(f);
        await Assert.ThrowsAsync<ArgumentException>(() => Derive(f));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store(f, Output(f)).WriteBatchAsync(ForexResearchFixture.Records()));
        await Assert.ThrowsAsync<InvalidDataException>(() => ForexDatasetSnapshot.ExportAsync(Output(f), Output(f, "subset"), "EURUSD", Start, Start.AddMinutes(4)));
        await Assert.ThrowsAsync<ArgumentException>(() => ForexDatasetDerivation.DeriveAsync(f.DatasetPath, Path.Combine(f.DatasetPath, "nested"), f.Trading, f.Aggregates,
            "EURUSD", Start, Start.AddMinutes(4), Coverage, new string('A', 64), new string('B', 64)));
    }
}
