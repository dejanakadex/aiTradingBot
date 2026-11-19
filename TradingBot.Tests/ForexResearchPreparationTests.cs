using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TradingBot.Application.Configuration;
using TradingBot.Infrastructure.Options;
using TradingBot.ShadowProbe;

namespace TradingBot.Tests;

public sealed class ForexResearchPreparationTests
{
    private static string Source(ForexResearchFixture f) => JsonSerializer.Serialize(new
    {
        TradingSettings = f.Trading, ForexPatterns = f.Patterns, ForexSignals = f.Signals, ShortAggregates = f.Aggregates
    });
    private static string Rules(ForexResearchFixture f) => JsonSerializer.Serialize(new
    { Passed = true, CanSendOrders = false, InstrumentId = "EURUSD", Snapshot = f.Profile.HistoricalRules[0] });

    [Fact]
    public void DraftPreservesStockAndThresholdsWithoutRulesOrApprovals()
    {
        using var f = new ForexResearchFixture();
        f.Trading.Instruments = [.. f.Trading.Instruments, new() { InstrumentId = "SPY", Symbol = "SPY", Enabled = false, TradingEnabled = false }];
        var source = Source(f);
        var result = ForexResearchPreparation.Prepare(source, null, 100);
        var json = JsonNode.Parse(result.Config)!;
        Assert.Equal(2, json["TradingSettings"]!["Instruments"]!.AsArray().Count);
        Assert.Empty(json["ForexResearch"]!["Profiles"]![0]!["HistoricalRules"]!.AsArray());
        Assert.Null(json["ForexSignals"]!["Profiles"]![0]!["ApprovedSignalVersion"]);
        Assert.Equal(100, json["ForexResearch"]!["Profiles"]![0]!["StartingEquityUsd"]!.GetValue<decimal>());
        Assert.False(json["TradingSettings"]!["Enabled"]!.GetValue<bool>());
        Assert.False(json["MarketDataCollection"]!["Enabled"]!.GetValue<bool>());
        Assert.Empty(json["ForexExecution"]!["Profiles"]!.AsArray());
        Assert.Equal(100, json["ResearchEvaluation"]!["MinimumTrainingSamples"]!.GetValue<int>());
        Assert.Equal(source, Source(f));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("failedEvidence")]
    [InlineData("wrongContract")]
    [InlineData("futureObservation")]
    public void UnsafeSourceOrEvidenceIsRejected(string fault)
    {
        using var f = new ForexResearchFixture();
        var source = JsonNode.Parse(Source(f))!; var evidence = JsonNode.Parse(Rules(f))!;
        if (fault == "live") source["TradingSettings"]!["LiveTradingExplicitlyEnabled"] = true;
        if (fault == "failedEvidence") evidence["Passed"] = false;
        if (fault == "wrongContract") evidence["Snapshot"]!["Contract"]!["Metadata"]!["Contract"]!["Currency"] = "GBP";
        if (fault == "futureObservation") evidence["Snapshot"]!["ObservedUtc"] = DateTime.UtcNow.AddDays(1);
        Assert.Throws<ArgumentException>(() => ForexResearchPreparation.Prepare(source.ToJsonString(), evidence.ToJsonString(), 100));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(50000)]
    public async Task PreparedConfigurationRunsExistingOfflineContractAndReplayWithoutOrders(int capital)
    {
        using var f = new ForexResearchFixture();
        await using (var db = f.CreateDbContext()) { await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync(); }
        var original = Rules(f);
        var prepared = ForexResearchPreparation.Prepare(Source(f), original, capital);
        await File.WriteAllTextAsync(f.ConfigPath, prepared.Config);
        var cfg = new ConfigurationBuilder().AddJsonFile(f.ConfigPath).Build();
        var p = cfg.GetSection("ForexResearch").Get<ForexResearchSettings>()!.Profiles.Single();
        Assert.Equal(f.Profile.HistoricalRules[0].ObservedUtc, p.HistoricalRules[0].ObservedUtc.ToUniversalTime());
        Assert.Equal(original, Rules(f));
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        string[] common = ["--db", f.DatabasePath, "--config", f.ConfigPath, "--instrument-id", "EURUSD", "--strategy-id", "patterns"];
        Assert.Equal(0, await ModelOpsProgram.RunAsync(["fx-contract", .. common]));
        Assert.Equal(0, await ModelOpsProgram.RunAsync(["fx-replay", .. common, "--dataset", f.DatasetPath,
            "--from", ForexResearchFixture.Start.ToString("O"), "--to", ForexResearchFixture.Start.AddSeconds(192).ToString("O")]));
        await using var verify = f.CreateDbContext();
        Assert.NotEmpty(await verify.CandidateLabelRecords.ToArrayAsync());
        Assert.Equal(0, await verify.OrderRecords.CountAsync());
        Assert.Equal(0, await verify.NumericalModelRecords.CountAsync());
        if (capital == 100)
            Assert.Contains(await verify.CandidateLabelRecords.Select(x => x.ReasonsJson).ToArrayAsync(), x => x.Contains("RiskRejected"));
    }

    [Fact]
    public async Task PrepareCliRefusesOverwriteAndCreatesNoDatabase()
    {
        using var f = new ForexResearchFixture();
        var sourcePath = f.ConfigPath + ".source.json";
        await File.WriteAllTextAsync(sourcePath, Source(f));
        string[] args = ["fx-prepare", "--config", sourcePath, "--equity-usd", "100", "--output", f.ConfigPath];
        Assert.Equal(0, await ModelOpsProgram.RunAsync(args));
        var first = await File.ReadAllBytesAsync(f.ConfigPath);
        Assert.Equal(1, await ModelOpsProgram.RunAsync(args));
        Assert.Equal(first, await File.ReadAllBytesAsync(f.ConfigPath));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(f.ConfigPath)!, "research.db")));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("execution")]
    public void RulesProbeRejectsInvalidReviewBeforeConnecting(string fault)
    {
        using var f = new ForexResearchFixture();
        var now = DateTime.UtcNow;
        if (fault == "execution") f.Trading.ForexPaperExecutionEnabled = true;
        var at = fault == "stale" ? now.AddHours(-25) : fault == "future" ? now.AddMinutes(1) : now;
        var json = JsonSerializer.Serialize(new { TradingSettings = f.Trading, ForexExecution = new ForexExecutionSettings
        { Profiles = [new() { InstrumentId = "EURUSD", MinimumBaseUnits = 20000, MaximumBaseUnits = 6000000, ReviewedAtUtc = at, EvidenceSource = "synthetic test" }] } });
        Assert.Throws<ArgumentException>(() => ForexRulesProbeRunner.ReadConfiguration(json, now));
    }
}
