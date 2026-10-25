using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using static TradingBot.Tests.ForexResearchFixture;

namespace TradingBot.Tests;

public sealed class ForexResearchTests
{
    [Fact]
    public async Task OfflineCliBindsHistoricalRulesAndRunsReplayWithoutBrokerServices()
    {
        using var f = new ForexResearchFixture();
        await using (var db = f.CreateDbContext()) { await db.Database.EnsureDeletedAsync(); await db.Database.MigrateAsync(); }
        await f.Dataset.WriteBatchAsync(Records());
        var json = System.Text.Json.JsonSerializer.Serialize(new { TradingSettings = f.Trading, RiskSettings = f.Limits,
            ForexResearch = new TradingBot.Infrastructure.Options.ForexResearchSettings { Profiles = [f.Profile] },
            ForexRisk = new TradingBot.Infrastructure.Options.ForexRiskSettings { Profiles = [f.Costs] }, ForexSignals = f.Signals,
            ForexPatterns = f.Patterns, ShortAggregates = f.Aggregates });
        await File.WriteAllTextAsync(f.ConfigPath, json);
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddJsonFile(f.ConfigPath).Build();
        var bound = Microsoft.Extensions.Configuration.ConfigurationBinder.Get<TradingBot.Infrastructure.Options.ForexResearchSettings>(configuration.GetSection("ForexResearch"), o => o.ErrorOnUnknownConfiguration = true);
        Assert.Single(Assert.Single(bound!.Profiles).HistoricalRules);
        var boundSignals = configuration.GetSection("ForexSignals").Get<TradingBot.Infrastructure.Options.ForexSignalSettings>()!;
        var boundPatterns = configuration.GetSection("ForexPatterns").Get<TradingBot.Infrastructure.Options.ForexPatternSettings>()!;
        boundPatterns.ReplaceConfiguredArrays(configuration.GetSection("ForexPatterns"));
        Assert.Equal(f.Signals.Profiles[0].Version, boundSignals.Profiles[0].Version);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(f.Patterns), System.Text.Json.JsonSerializer.Serialize(boundPatterns));
        string[] common = ["--db", f.DatabasePath, "--config", f.ConfigPath, "--instrument-id", "EURUSD", "--strategy-id", "patterns"];
        Assert.Equal(0, await ModelOpsProgram.RunAsync(["fx-contract", .. common]));
        Assert.Equal(0, await ModelOpsProgram.RunAsync(["fx-replay", .. common, "--dataset", f.DatasetPath, "--from", Start.ToString("O"), "--to", Start.AddSeconds(192).ToString("O")]));
        await using var verify = f.CreateDbContext();
        Assert.True((await verify.ReplayBacktestRecords.SingleAsync()).ClosedTradeCount > 0,
            string.Join("; ", (await verify.ResearchCandidateRecords.Where(x => x.Outcome == ResearchCandidateOutcome.Blocked).Select(x => x.ReasonsJson).ToArrayAsync()).Distinct()));
        Assert.Equal(0, await verify.OrderRecords.CountAsync());
    }

    [Theory]
    [InlineData("Target", 1.104)]
    [InlineData("Stop", 1.098)]
    [InlineData("Timer", 1.100)]
    public void ExecutableBidAskExitsAndFeesDetermineLabel(string exit, double price)
    {
        using var f = new ForexResearchFixture();
        var quotes = Enumerable.Range(0, 22).Select(i => i < 2 ? Q(i) : Q(i, (decimal)price, (decimal)price + .00002m)).ToArray();
        var r = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start, quotes, [], f.Profile, f.Costs, 100000);
        Assert.Equal("Closed", r.Trade.Status); Assert.Equal(exit, r.Trade.ExitReason);
        Assert.Equal(1.10002m, r.Trade.EntryPrice); Assert.Equal(20000, r.Trade.FilledQuantity);
        Assert.Equal(4, r.Trade.Commission);
        Assert.Equal(r.GrossReturnBps - r.CostBps, r.NetReturnBps);
        Assert.Equal((r.Trade.ExitPrice - r.Trade.EntryPrice) * 20000 - 4, r.Trade.NetPnl);
        Assert.True(r.Trade.ExitTimeUtc > r.Trade.EntryTimeUtc);
        if (exit == "Timer") Assert.Equal(Start.AddSeconds(12), r.Trade.ExitTimeUtc); // first fill + 10s + 250ms, next quote
        if (exit == "Target") Assert.NotEqual(r.GrossReturnBps, r.Trade.HorizonReturnBps);
    }

    [Fact]
    public void PartialFillsPayConservativePerFillFeesAndTimerCancelsUnfilledRemainder()
    {
        using var f = new ForexResearchFixture(); f.Profile.FillBaseUnitsPerQuote = 1000; f.Profile.HoldingSeconds = 2;
        var r = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start, Enumerable.Range(0, 22).Select(i => Q(i)).ToArray(), [], f.Profile, f.Costs, 100000);
        Assert.Equal("Closed", r.Trade.Status); Assert.Equal(2000, r.Trade.FilledQuantity); Assert.Equal(2000, r.Trade.ExitedQuantity);
        Assert.Equal(8, r.Trade.Commission); Assert.Equal("Timer", r.Trade.ExitReason);
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("reconnect")]
    [InlineData("stale")]
    [InlineData("truncated")]
    [InlineData("futureRules")]
    [InlineData("short")]
    public void MissingCoverageOrPermissionNeverCreatesTrainingLabel(string fault)
    {
        using var f = new ForexResearchFixture(); var quotes = Enumerable.Range(0, 22).Select(i => Q(i)).ToArray();
        if (fault == "gap") quotes = quotes.Where(x => x.AvailableUtc < Start.AddSeconds(2) || x.AvailableUtc > Start.AddSeconds(8)).ToArray();
        if (fault == "truncated") quotes = quotes.Take(2).ToArray();
        if (fault == "reconnect") quotes[2] = quotes[2] with { Id = "IBKR:FXQ:new:00002" };
        if (fault == "stale") quotes[2] = quotes[2] with { EventUtc = Start.AddSeconds(-10) };
        if (fault == "futureRules") f.Profile.HistoricalRules = [f.Profile.HistoricalRules[0] with { ObservedUtc = Start.AddSeconds(1) }];
        var r = f.Simulator().Simulate(Guid.Empty, fault == "short" ? TradeDirection.Short : TradeDirection.Long, Start, quotes, [], f.Profile, f.Costs, 100000);
        Assert.Equal("Incomplete", r.Trade.Status); Assert.Null(r.NetReturnBps); Assert.Null(r.CostBps);
    }

    [Fact]
    public void TrailingUsesOnlyAvailableAtrAndDoesNotTakeFixedTarget()
    {
        using var f = new ForexResearchFixture(); f.Profile.Exit.Mode = ExitStrategyMode.AtrTrailing;
        var quotes = Enumerable.Range(0, 22).Select(i => i < 2 ? Q(i) : i < 5 ? Q(i, 1.104m, 1.10402m) : Q(i, 1.103m, 1.10302m)).ToArray();
        var r = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start, quotes, [new(Start.AddSeconds(2), .0002m)], f.Profile, f.Costs, 100000);
        Assert.Equal("Stop", r.Trade.ExitReason); Assert.Equal(Start.AddSeconds(6), r.Trade.ExitTimeUtc);
        var future = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start, quotes, [new(Start.AddSeconds(20), .0002m)], f.Profile, f.Costs, 100000);
        Assert.Equal("Timer", future.Trade.ExitReason);
    }

    [Fact]
    public void PairReaderRejectsCorruptionAndIgnoresQuoteSizesForLiquidity()
    {
        var rows = Records(); var quotes = ForexTradeSimulator.ReadQuotes(rows, "EURUSD", "EURUSD");
        Assert.Equal(rows.Length / 2, quotes.Length);
        Assert.Throws<InvalidDataException>(() => ForexTradeSimulator.ReadQuotes(rows.Skip(1).ToArray(), "EURUSD", "EURUSD"));
        rows[0].Size = 0; Assert.Equal(quotes, ForexTradeSimulator.ReadQuotes(rows, "EURUSD", "EURUSD"));
        rows[0].Price = 2; Assert.Throws<InvalidDataException>(() => ForexTradeSimulator.ReadQuotes(rows, "EURUSD", "EURUSD"));
    }

    [Fact]
    public async Task ParquetReplayIsDeterministicAndProducesIsolatedExecutionLabels()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        var request = new ReplayStartRequest("EURUSD", Start, Start.AddSeconds(192)) { StrategyId = "patterns" };
        var first = await f.Replay().RunForexResearchAsync(request); var second = await f.Replay().RunForexResearchAsync(request);
        Assert.True(first.ClosedTradeCount > 0, string.Join(";", first.Trades.Select(x => x.ExitReason)));
        Assert.Equal(first.InputSha256, second.InputSha256); Assert.Equal(first.OutputSha256, second.OutputSha256);
        await using var db = f.CreateDbContext();
        var labels = await db.CandidateLabelRecords.Include(x => x.ResearchCandidate).ToArrayAsync();
        Assert.NotEmpty(labels); Assert.All(labels, x => Assert.StartsWith(ForexResearchContract.Labels, x.LabelVersion));
        Assert.All(labels, x => Assert.NotNull(x.ResearchCandidate.ReplayRunId));
        Assert.Contains(labels, x => x.Status == CandidateLabelStatus.Complete);
        Assert.All(labels.Where(x => x.Status == CandidateLabelStatus.Complete), x => Assert.Equal(x.GrossReturnBps - x.EstimatedCostBps, x.NetReturnBps));
        Assert.Equal(0, await f.Research().LabelMatureCandidatesAsync(Start.AddDays(1), first.ReplayRunId));
        Assert.Empty(await db.OrderRecords.ToArrayAsync());
        var oldVersion = f.Contract().Get("EURUSD", "patterns").Label; f.Profile.LatencyMilliseconds++;
        Assert.NotEqual(oldVersion, f.Contract().Get("EURUSD", "patterns").Label);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Replay().ResumeAsync(first.ReplayRunId));
    }
}

