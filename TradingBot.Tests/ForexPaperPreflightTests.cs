using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class ForexPaperPreflightTests
{
    [Fact]
    public async Task CliReportsMissingPrerequisitesWithoutCreatingDatabaseOrOverwritingEvidence()
    {
        using var f = new ForexResearchFixture();
        await File.WriteAllTextAsync(f.ConfigPath, "{}");
        var missingDatabase = Path.Combine(Path.GetDirectoryName(f.ConfigPath)!, "missing.db");
        var output = Path.Combine(Path.GetDirectoryName(f.ConfigPath)!, "preflight.json");
        string[] args = ["fx-paper-preflight", "--db", missingDatabase, "--config", f.ConfigPath,
            "--instrument-id", "EURUSD", "--strategy-id", "patterns", "--output", output];
        Assert.Equal(3, await ModelOpsProgram.RunAsync(args));
        Assert.False(File.Exists(missingDatabase));
        var original = await File.ReadAllTextAsync(output);
        using var json = JsonDocument.Parse(original);
        Assert.False(json.RootElement.GetProperty("canSendOrders").GetBoolean());
        Assert.False(json.RootElement.GetProperty("offlinePrerequisitesPassed").GetBoolean());
        Assert.Equal(1, await ModelOpsProgram.RunAsync(args));
        Assert.Equal(original, await File.ReadAllTextAsync(output));
    }

    [Theory]
    [InlineData("2026-10-25T18:30:00Z", false, "2026-10-25T21:15:00Z")]
    [InlineData("2026-11-01T18:30:00Z", false, "2026-11-01T22:15:00Z")]
    [InlineData("2026-10-27T12:00:00Z", true, null)]
    public async Task MissingDatabaseNeverGetsCreatedAndClosedSessionHasScheduledNextOpen(string instant, bool open, string? next)
    {
        var path = Path.Combine(Path.GetTempPath(), "fx12-missing-" + Guid.NewGuid().ToString("N") + ".db");
        var report = await ForexPaperPreflight.CheckAsync(new ConfigurationBuilder().Build(), path, "EURUSD", "patterns", DateTime.Parse(instant).ToUniversalTime());
        Assert.False(File.Exists(path)); Assert.False(report.OfflinePrerequisitesPassed); Assert.False(report.CanSendOrders);
        Assert.Contains(report.Checks, x => x.Code == "database" && !x.Passed);
        Assert.Equal(open, report.SessionOpen); Assert.Equal(next == null ? (DateTime?)null : DateTime.Parse(next).ToUniversalTime(), report.NextScheduledSessionUtc);
    }

    [Theory]
    [InlineData("2026-10-25T18:00:00Z", true)]
    [InlineData("2026-10-25T20:00:00+02:00", true)]
    [InlineData("2026-10-25T18:00:00", false)]
    public void ReviewedTimestampBindingPreservesInstantButRejectsMissingOffset(string text, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ForexExecution:Profiles:0:InstrumentId"] = "EURUSD", ["ForexExecution:Profiles:0:MinimumBaseUnits"] = "1000",
            ["ForexExecution:Profiles:0:MaximumBaseUnits"] = "20000", ["ForexExecution:Profiles:0:EvidenceSource"] = "test",
            ["ForexExecution:Profiles:0:ReviewedAtUtc"] = text }).Build();
        var settings = config.GetSection("ForexExecution").Get<ForexExecutionSettings>()!;
        settings.NormalizeBoundTimestamps();
        if (!valid) { Assert.Throws<ArgumentException>(settings.Validate); return; }
        settings.Validate(); Assert.Equal(new DateTime(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc), settings.Profiles[0].ReviewedAtUtc);
        Assert.Equal(DateTimeKind.Utc, settings.Profiles[0].ReviewedAtUtc.Kind);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("stale-rollout")]
    [InlineData("changed-model")]
    [InlineData("unreviewed-size")]
    [InlineData("live")]
    public async Task OfflineChecksReadExistingEvidenceWithoutChangingDatabaseOrGrantingExecution(string scenario)
    {
        using var f = new ForexResearchFixture();
        var registry = new InstrumentRegistryService(f, Options.Create(f.Trading), f); await registry.SynchronizeConfiguredAsync();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var replay = await f.Replay().RunForexResearchAsync(new("EURUSD", ForexResearchFixture.Start, ForexResearchFixture.Start.AddSeconds(192), StrategyId: "patterns"));
        var c = f.Contract().Get("EURUSD", "patterns");
        await using (var db = f.CreateDbContext())
        {
            (await db.InstrumentRegistryRecords.SingleAsync()).Status = InstrumentOnboardingStatus.PaperReady;
            db.InstrumentRolloutEvaluationRecords.Add(new() { InstrumentId = "EURUSD", StatusAfter = InstrumentOnboardingStatus.PaperReady,
                EvaluatedAtUtc = scenario == "stale-rollout" ? f.UtcNow.AddMinutes(-2) : f.UtcNow });
            var evaluation = new ResearchEvaluationRunRecord { Id = Guid.NewGuid(), RunKey = "fx12-test", ReplayRunId = replay.ReplayRunId,
                InstrumentId = "EURUSD", StrategyId = "patterns", HorizonSeconds = 10, FeatureVersion = c.Feature, PatternVersion = c.Pattern,
                LabelVersion = c.Label, EvaluationVersion = ResearchEvaluationVersion.Create(new()), CompletedAtUtc = f.UtcNow, InputSha256 = "synthetic" };
            evaluation.OutputSha256 = ResearchEvaluationService.ComputeOutputHash(evaluation); db.ResearchEvaluationRunRecords.Add(evaluation);
            var model = new NumericalModelRecord { Id = Guid.NewGuid(), ModelVersion = 1, Status = NumericalModelStatus.Approved, EvaluationRunId = evaluation.Id,
                InstrumentId = "EURUSD", StrategyId = "patterns", HorizonSeconds = 10, FeatureVersion = c.Feature, PatternVersion = c.Pattern, LabelVersion = c.Label,
                ModelVersionTag = ForexResearchContract.Model, ApprovalReady = true, ModelArtifact = [1], InputSha256 = "synthetic" };
            db.NumericalModelRecords.Add(model); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            model = await db.NumericalModelRecords.SingleAsync(); model.OutputSha256 = NumericalModelService.OutputHash(model);
            if (scenario == "changed-model") model.ModelArtifact = [2];
            await db.SaveChangesAsync();
        }
        if (scenario == "live") { f.Trading.OperatingMode = TradingOperatingMode.LiveTrading; f.Trading.LiveTradingExplicitlyEnabled = true; }
        var execution = new ForexExecutionSettings { Profiles = scenario == "unreviewed-size" ? [] : [new() { InstrumentId = "EURUSD",
            MinimumBaseUnits = 1000, MaximumBaseUnits = 20000, ReviewedAtUtc = f.UtcNow, EvidenceSource = "synthetic test" }] };
        var json = JsonSerializer.Serialize(new { TradingSettings = f.Trading, RiskSettings = f.Limits, ForexResearch = new ForexResearchSettings { Profiles = [f.Profile] },
            ForexRisk = new ForexRiskSettings { Profiles = [f.Costs] }, ForexSignals = f.Signals, ForexPatterns = f.Patterns, ForexExecution = execution, ShortAggregates = f.Aggregates });
        await File.WriteAllTextAsync(f.ConfigPath, json);
        var config = new ConfigurationBuilder().AddJsonFile(f.ConfigPath).Build();
        var before = SHA256.HashData(await File.ReadAllBytesAsync(f.DatabasePath));
        var report = await ForexPaperPreflight.CheckAsync(config, f.DatabasePath, "EURUSD", "patterns", f.UtcNow);
        Assert.Equal(scenario == "valid", report.OfflinePrerequisitesPassed);
        Assert.False(report.CanSendOrders); Assert.NotEmpty(report.RuntimeChecksStillRequired);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(f.DatabasePath)));
        var output = JsonSerializer.Serialize(report);
        Assert.DoesNotContain(f.DatabasePath, output); Assert.DoesNotContain("synthetic test", output);
    }
}
