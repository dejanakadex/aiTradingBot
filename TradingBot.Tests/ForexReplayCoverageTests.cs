using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using static TradingBot.Tests.ForexResearchFixture;

namespace TradingBot.Tests;

public sealed class ForexReplayCoverageTests
{
    [Fact]
    public void UnchangedPricesWithObservedUpdatesCanCloseButStoredQuoteAloneCannotFill()
    {
        using var f = new ForexResearchFixture();
        var complete = Run(f, Enumerable.Range(0, 22).Select(i => Q(i)).ToArray());
        Assert.Equal("Closed", complete.Trade.Status);
        Assert.Equal("Timer", complete.Trade.ExitReason);
        Assert.Null(complete.Trade.CoverageIssue);
        var noUpdates = Run(f, [Q(0)]);
        Assert.Equal(0, noUpdates.Trade.FilledQuantity);
        Assert.Null(noUpdates.NetReturnBps);
        Assert.Equal("QuoteDatasetBoundaryBeforeEntryExpiry", noUpdates.Trade.CoverageIssue!.Reason);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ObservationGapThresholdIsInclusiveAndDoesNotClaimNetworkFailure(int extraTicks, bool incomplete)
    {
        using var f = new ForexResearchFixture();
        var q = Q(4) with { AvailableUtc = Start.AddSeconds(4).AddTicks(extraTicks) };
        var quotes = new[] { Q(0), Q(1), q }.Concat(Enumerable.Range(5, 17).Select(i => Q(i))).ToArray();
        var r = Run(f, quotes);
        Assert.Equal(incomplete ? "Incomplete" : "Closed", r.Trade.Status);
        if (!incomplete) { Assert.Null(r.Trade.CoverageIssue); return; }
        var issue = r.Trade.CoverageIssue!;
        Assert.Equal("UnobservedQuoteInterval", issue.Reason);
        Assert.Null(issue.RecordedInterruption);
        Assert.Equal(Start.AddSeconds(1), issue.PreviousQuote!.AvailableUtc);
        Assert.Equal(q.AvailableUtc, issue.CurrentQuote!.AvailableUtc);
        Assert.Null(r.Trade.NetPnl);
        Assert.Equal(0, r.Trade.ExitedQuantity);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void DecisionAgeThresholdIsInclusiveWithoutReusingQuoteForFill(int extraTicks, bool stale)
    {
        using var f = new ForexResearchFixture(); f.Costs.MaximumSnapshotAgeSeconds = 2;
        var r = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start.AddSeconds(2).AddTicks(extraTicks),
            [Q(0)], [], f.Profile, f.Costs, 100000);
        Assert.Equal(0, r.Trade.FilledQuantity);
        Assert.Equal(stale ? "StaleDecisionQuote" : "QuoteDatasetBoundaryBeforeEntryExpiry", r.Trade.CoverageIssue!.Reason);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ReceivedQuoteAgeIsSeparateFromTimeBetweenUpdates(int extraTicks, bool stale)
    {
        var policy = new ForexReplayCoveragePolicy(3, 2);
        var q = Q(3) with { EventUtc = Start.AddSeconds(1).AddTicks(-extraTicks) };
        var issue = policy.Between(Q(2), q);
        Assert.Equal(stale, issue != null);
        if (stale) Assert.Equal("StaleReceivedQuote", issue!.Reason);
    }

    [Fact]
    public void ShortRecordedInterruptionBlocksExitEvenWithinGapLimitAndWithoutNewSessionId()
    {
        using var f = new ForexResearchFixture();
        var evidence = Evidence([new(Start.AddSeconds(1.2), Start.AddSeconds(1.8), "Recorded broker disconnect")]);
        var r = Run(f, Enumerable.Range(0, 22).Select(i => Q(i)).ToArray(), evidence);
        Assert.Equal("Incomplete", r.Trade.Status);
        Assert.Equal(20000, r.Trade.FilledQuantity);
        Assert.Equal(0, r.Trade.ExitedQuantity);
        Assert.Null(r.NetReturnBps);
        Assert.Equal("RecordedInterruption", r.Trade.CoverageIssue!.Reason);
        Assert.Equal(evidence.Interruptions[0], r.Trade.CoverageIssue.RecordedInterruption);
        Assert.Null(r.Trade.HorizonReturnBps);
    }

    [Fact]
    public void DecisionCannotUseQuoteFromBeforeRecordedInterruptionButFreshPostRecoveryQuoteCanBeUsed()
    {
        var policy = new ForexReplayCoveragePolicy(3, 5,
            Evidence([new(Start.AddMilliseconds(200), Start.AddMilliseconds(800), "Socket interruption")]));
        Assert.Equal("RecordedInterruption", policy.AtDecision(Q(0), Start.AddSeconds(1))!.Reason);
        Assert.Null(policy.AtDecision(Q(1), Start.AddSeconds(1.5)));
        // A tolerated future event timestamp cannot hide an interruption after actual receipt.
        var future = Q(0) with { EventUtc = Start.AddMilliseconds(250), AvailableUtc = Start.AddMilliseconds(250) };
        var brief = new ForexReplayCoveragePolicy(3, 5,
            Evidence([new(Start.AddMilliseconds(50), Start.AddMilliseconds(100), "Brief interruption")]));
        Assert.Equal("RecordedInterruption", brief.AtDecision(future, Start.AddMilliseconds(300))!.Reason);
    }

    [Fact]
    public void ReconnectBoundaryIsNotSilentlyBridgedEvenWithUnchangedPrice()
    {
        using var f = new ForexResearchFixture();
        var quotes = Enumerable.Range(0, 22).Select(i => i < 2 ? Q(i) : Q(i) with { Id = $"IBKR:FXQ:new:{i:D5}" }).ToArray();
        var r = Run(f, quotes);
        Assert.Equal("CallbackSessionChanged", r.Trade.CoverageIssue!.Reason);
        Assert.Null(r.Trade.CoverageIssue.RecordedInterruption); // Callback identity alone does not establish the cause.
        Assert.Equal(0, r.Trade.ExitedQuantity);
        Assert.Null(r.NetReturnBps);
    }

    [Fact]
    public void DatasetEndingAfterEntryDoesNotInventTimerExitOrMarkToMarketProfit()
    {
        using var f = new ForexResearchFixture();
        var r = Run(f, [Q(0), Q(1)]);
        Assert.Equal("QuoteDatasetBoundaryWithOpenPosition", r.Trade.CoverageIssue!.Reason);
        Assert.Equal(20000, r.Trade.FilledQuantity);
        Assert.Null(r.Trade.ExitTimeUtc);
        Assert.Null(r.Trade.NetPnl);
        Assert.Null(r.NetReturnBps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedDisconnectWithoutRecoveryIsReportedEvenWhenNoNextQuoteExists(bool filled)
    {
        using var f = new ForexResearchFixture();
        var evidence = Evidence([new(Start.AddSeconds(1.2), Start.AddSeconds(30), "Disconnected without recovery in simulation window")]);
        var r = Run(f, filled ? [Q(0), Q(1)] : [Q(0)], evidence);
        Assert.Equal("RecordedInterruption", r.Trade.CoverageIssue!.Reason);
        Assert.Null(r.Trade.CoverageIssue.CurrentQuote);
        Assert.Equal(filled ? 20000m : 0m, r.Trade.FilledQuantity);
        Assert.Equal(0, r.Trade.ExitedQuantity);
        Assert.Null(r.Trade.NetPnl);
    }

    [Fact]
    public void CompleteEntryWindowWithoutLimitFillIsNotReportedAsDatasetTruncation()
    {
        using var f = new ForexResearchFixture();
        var r = Run(f, new[] { Q(0) }.Concat(Enumerable.Range(1, 21).Select(i => Q(i, 1.101m, 1.10102m))).ToArray());
        Assert.Equal(0, r.Trade.FilledQuantity);
        Assert.Equal("NoFillWithinEntryWindow", r.Trade.CoverageIssue!.Reason);
        Assert.Null(r.NetReturnBps);
    }

    [Fact]
    public void CallbackTimesArePreservedAndFutureToleratedQuoteCannotBeUsedBeforeEventTime()
    {
        var rows = Records().Take(2).ToArray();
        foreach (var row in rows) row.ReceivedTimeUtc = row.EventTimeUtc.AddMilliseconds(-100);
        var q = Assert.Single(ForexTradeSimulator.ReadQuotes(rows, "EURUSD", "EURUSD",
            new ShortAggregateSettings { ForexFutureToleranceMilliseconds = 250 }));
        Assert.Equal(rows[0].ReceivedTimeUtc, q.ReceivedUtc);
        Assert.Equal(rows[0].EventTimeUtc, q.EventUtc);
        Assert.Equal(q.EventUtc, q.AvailableUtc);
        using var f = new ForexResearchFixture();
        var r = f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, q.ReceivedUtc, [q], [], f.Profile, f.Costs, 100000);
        Assert.Equal("NoDecisionQuote", r.Trade.CoverageIssue!.Reason);
        Assert.Equal(0, r.Trade.FilledQuantity);
    }

    [Fact]
    public void ClosedMarketIsDistinctFromUnobservedInterval()
    {
        var saturday = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var previous = Q(0) with { EventUtc = saturday, ReceivedUtc = saturday, AvailableUtc = saturday };
        var next = previous with { EventUtc = saturday.AddSeconds(1), ReceivedUtc = saturday.AddSeconds(1), AvailableUtc = saturday.AddSeconds(1) };
        Assert.Equal("MarketSessionClosed", new ForexReplayCoveragePolicy(3, 2).Between(previous, next)!.Reason);
    }

    [Theory]
    [InlineData("instrument")]
    [InlineData("range")]
    [InlineData("timestamp")]
    [InlineData("interval")]
    [InlineData("source")]
    public void FrozenEvidenceRejectsWrongScopeAndAmbiguousTimes(string fault)
    {
        var evidence = Evidence([new(Start.AddSeconds(1), Start.AddSeconds(2), "Disconnect")]);
        evidence = fault switch
        {
            "instrument" => evidence with { InstrumentId = "SPY" },
            "range" => evidence with { ToUtc = Start.AddSeconds(1) },
            "timestamp" => evidence with { FromUtc = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) },
            "interval" => evidence with { Interruptions = [new(Start.AddSeconds(2), Start.AddSeconds(1), "Disconnect")] },
            _ => evidence with { Source = "" }
        };
        Assert.Throws<InvalidDataException>(() => ForexReplayCoveragePolicy.FreezeEvidence(evidence, "EURUSD", Start, Start.AddSeconds(22)));
    }

    [Fact]
    public async Task FrozenEvidenceChangesReplayHashAndPersistsDiagnosticsWithoutTouchingOldRun()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        var request = new ReplayStartRequest("EURUSD", Start, Start.AddSeconds(192), StrategyId: "patterns");
        var original = await f.Replay().RunForexResearchAsync(request);
        var interrupted = request with { ForexCoverage = Evidence([new(Start.AddSeconds(1), Start.AddSeconds(191), "Test interruption")]) };
        var first = await f.Replay().RunForexResearchAsync(interrupted);
        var repeat = await f.Replay().RunForexResearchAsync(interrupted);
        Assert.NotEqual(original.InputSha256, first.InputSha256);
        Assert.Equal(first.InputSha256, repeat.InputSha256);
        Assert.Equal(first.OutputSha256, repeat.OutputSha256);
        Assert.Equal(0, first.ClosedTradeCount);
        Assert.Contains(first.Trades, x => x.CoverageIssue?.Reason == "RecordedInterruption");
        await using var db = f.CreateDbContext();
        Assert.Equal(original.OutputSha256, (await db.ReplayBacktestRecords.SingleAsync(x => x.Id == original.Id)).OutputSha256);
        Assert.Contains(await db.CandidateLabelRecords.Include(x => x.ResearchCandidate)
            .Where(x => x.ResearchCandidate.ReplayRunId == first.ReplayRunId).Select(x => x.ReasonsJson).ToArrayAsync(),
            x => x.Contains("RecordedInterruption") && x.Contains("ReceivedUtc"));
        Assert.Empty(await db.OrderRecords.ToArrayAsync());
    }

    [Fact]
    public async Task LegacyFxReplayCannotResumeAsStockAfterLabelVersionChange()
    {
        using var f = new ForexResearchFixture(); await f.Dataset.WriteBatchAsync(Records());
        var run = await f.Replay().RunForexResearchAsync(new("EURUSD", Start, Start.AddSeconds(192), StrategyId: "patterns"));
        await using (var db = f.CreateDbContext())
        {
            var row = await db.ReplayRunRecords.SingleAsync();
            row.StrategyVersion = "labels-fx-execution-v1+config-old"; row.Status = ReplayRunStatus.Paused;
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Replay().ResumeAsync(run.ReplayRunId));
    }

    private static ForexReplayCoverageEvidence Evidence(ForexReplayInterruption[] interruptions) =>
        new("EURUSD", Start, Start.AddSeconds(192), "Synthetic frozen connectivity log", interruptions);
    private static ForexSimulationResult Run(ForexResearchFixture f, ForexReplayQuote[] quotes, ForexReplayCoverageEvidence? evidence = null) =>
        f.Simulator().Simulate(Guid.Empty, TradeDirection.Long, Start, quotes, [], f.Profile, f.Costs, 100000, coverageEvidence: evidence);
}
