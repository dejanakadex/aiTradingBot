using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexDatasetReviewTests
{
    private static readonly DateTime Start = ForexResearchFixture.Start;
    private static ResearchEvaluationSettings Evaluation => new() { TrainingWindowDays = 2, ValidationWindowDays = 1, StepDays = 1, HoldoutDays = 1 };
    private static ForexDatasetPlan Plan => new("EURUSD", "patterns", Start.AddDays(-1), Start,
        Start.AddDays(2), Start.AddDays(3), Start.AddDays(4), 120);

    [Fact]
    public async Task StreamingReviewPreservesHashesAndReportsExpiredRulesWithoutApprovingDataset()
    {
        using var f = new ForexResearchFixture();
        var rows = ForexResearchFixture.Records();
        await f.Dataset.WriteBatchAsync(rows);
        var later = ForexResearchFixture.Records();
        foreach (var r in later) { r.EventId = r.EventId.Replace("fx10", "later"); r.EventTimeUtc = r.EventTimeUtc.AddDays(2); r.ReceivedTimeUtc = r.ReceivedTimeUtc.AddDays(2); }
        await f.Dataset.WriteBatchAsync(later);
        var before = await f.Dataset.VerifyAsync();
        var report = JsonSerializer.SerializeToElement(await ForexDatasetReview.AnalyzeAsync(f.Dataset, f.Trading, f.Aggregates,
            f.Profile, f.Costs, Plan, Evaluation, Start, Start.AddDays(2).AddMinutes(4)));
        Assert.Equal(608, report.GetProperty("EventCount").GetInt32());
        Assert.False(report.GetProperty("N5Complete").GetBoolean());
        Assert.False(report.GetProperty("CanSendOrders").GetBoolean());
        var days = report.GetProperty("Days").EnumerateArray().ToArray();
        Assert.Equal(2, days.Length);
        Assert.Equal(0, days[0].GetProperty("RulesUnavailablePairs").GetInt64());
        Assert.Equal(152, days[1].GetProperty("RulesUnavailablePairs").GetInt64());
        Assert.Equal(.2m, days[0].GetProperty("MeanSpreadPips").GetDecimal());
        Assert.True(days[1].GetProperty("MaximumGapSeconds").GetDouble() > 86400);
        Assert.Equal(before.ManifestSha256, (await f.Dataset.VerifyAsync()).ManifestSha256);
        using var db = f.CreateDbContext(); Assert.Empty(db.ReplayRunRecords); Assert.Empty(db.OrderRecords);
    }

    [Fact]
    public async Task HoldoutAndInvalidPlanRejectedBeforeReadingDataset()
    {
        using var f = new ForexResearchFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => ForexDatasetReview.AnalyzeAsync(f.Dataset, f.Trading, f.Aggregates,
            f.Profile, f.Costs, Plan, Evaluation, Start, Plan.HoldoutFromUtc.AddTicks(1)));
        Assert.Throws<ArgumentException>(() => (Plan with { BoundaryBufferSeconds = 1 }).Validate(Evaluation, f.Profile));
        Assert.Throws<ArgumentException>(() => (Plan with { ReservedUtc = Start.AddSeconds(1) }).Validate(Evaluation, f.Profile));
        Assert.Throws<ArgumentException>(() => (Plan with { ValidationFromUtc = Start.AddDays(1) }).Validate(Evaluation, f.Profile));
    }

    [Fact]
    public void ActualLabelEndsAndEmbargoKeepPartitionsSeparate()
    {
        Assert.True(Plan.ContainsLabel("training", Start.AddHours(1), Start.AddHours(1).AddSeconds(10)));
        Assert.False(Plan.ContainsLabel("training", Start.AddSeconds(119), Start.AddMinutes(5)));
        Assert.False(Plan.ContainsLabel("training", Plan.ValidationFromUtc.AddMinutes(-3), Plan.ValidationFromUtc.AddTicks(1)));
        Assert.False(Plan.ContainsLabel("training", Plan.ValidationFromUtc.AddSeconds(-60), Plan.ValidationFromUtc));
        Assert.False(Plan.ContainsLabel("training", Start.AddHours(1), null));
        Assert.False(Plan.ContainsLabel("validation", Start.AddHours(1), Start.AddHours(2)));
        Assert.False(Plan.ContainsLabel("holdout", Plan.HoldoutFromUtc.AddHours(1), Plan.HoldoutToUtc.AddSeconds(1)));
    }

    [Fact]
    public async Task IncompleteQuotePairsFailInsteadOfReceivingDatasetApproval()
    {
        using var f = new ForexResearchFixture();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records().Skip(1).ToArray());
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ForexDatasetReview.AnalyzeAsync(f.Dataset, f.Trading, f.Aggregates,
            f.Profile, f.Costs, Plan, Evaluation, Start, Start.AddMinutes(4)));
        Assert.Contains("Incomplete FX quote pairs: 1", ex.Message);
    }

    [Fact]
    public async Task SnapshotContainingHoldoutIsRejectedEvenForEarlierRequestedRange()
    {
        using var f = new ForexResearchFixture();
        var rows = ForexResearchFixture.Records();
        foreach (var r in rows) { r.EventTimeUtc = r.EventTimeUtc.AddDays(3); r.ReceivedTimeUtc = r.ReceivedTimeUtc.AddDays(3); }
        await f.Dataset.WriteBatchAsync(rows);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ForexDatasetReview.AnalyzeAsync(f.Dataset, f.Trading, f.Aggregates,
            f.Profile, f.Costs, Plan, Evaluation, Start, Start.AddMinutes(4)));
        Assert.Contains("holdout", ex.Message);
    }

    [Theory]
    [InlineData("fx-dataset-review")]
    [InlineData("fx-collection-stream-audit")]
    [InlineData("fx-derive")]
    public async Task CliWritesBoundReportWithoutOperationalDatabaseAndRefusesOverwrite(string command)
    {
        using var f = new ForexResearchFixture();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var directory = Path.GetDirectoryName(f.DatabasePath)!;
        var planPath = Path.Combine(directory, "plan.json"); var output = Path.Combine(directory, "review.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(Plan));
        await File.WriteAllTextAsync(f.ConfigPath, JsonSerializer.Serialize(new
        {
            TradingSettings = f.Trading, ShortAggregates = f.Aggregates, ResearchEvaluation = Evaluation,
            ForexResearch = new { Profiles = new[] { f.Profile } }, ForexRisk = new { Profiles = new[] { f.Costs } }
        }));
        string[] args = [command, "--dataset", f.DatasetPath, "--config", f.ConfigPath, "--plan", planPath,
            "--from", Start.ToString("O"), "--to", Start.AddMinutes(4).ToString("O"), "--output", output];
        if (command == "fx-derive")
        {
            var coverage = Path.Combine(directory, "coverage.json");
            await File.WriteAllTextAsync(coverage, JsonSerializer.Serialize(new TradingBot.Application.DTOs.ForexReplayCoverageEvidence(
                "EURUSD", Start, Start.AddMinutes(4), "Frozen test coverage", [])));
            args = [.. args[..^2], "--coverage", coverage, "--derived-dataset", Path.Combine(directory, "derived"), "--output", output];
        }
        Assert.Equal(0, await ModelOpsProgram.RunAsync(args));
        var original = await File.ReadAllTextAsync(output);
        Assert.Contains("planSha256", original);
        Assert.NotEqual(0, await ModelOpsProgram.RunAsync(args));
        Assert.Equal(original, await File.ReadAllTextAsync(output));
        args[^1] = Path.Combine(directory, "holdout-review.json");
        args[10] = Plan.HoldoutFromUtc.AddSeconds(1).ToString("O");
        Assert.NotEqual(0, await ModelOpsProgram.RunAsync(args));
        Assert.False(File.Exists(args[^1]));
        args[10] = Start.AddMinutes(4).ToString("O");
        var holdout = ForexResearchFixture.Records();
        foreach (var row in holdout) { row.EventTimeUtc += TimeSpan.FromDays(3); row.ReceivedTimeUtc += TimeSpan.FromDays(3); }
        await f.Dataset.WriteBatchAsync(holdout);
        Assert.NotEqual(0, await ModelOpsProgram.RunAsync(args));
        Assert.False(File.Exists(args[^1]));
    }
}
