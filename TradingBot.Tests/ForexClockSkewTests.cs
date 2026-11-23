using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexClockSkewTests
{
    [Fact]
    public async Task CheckpointedForexQuotesPreserveQualityAndAggregateParity()
    {
        using var bufferedDb = new ForexResearchFixture(); using var strictDb = new ForexResearchFixture();
        var trading = ForexCollectionTests.Settings();
        using var buffered = new MarketDataQualityService(bufferedDb, Options.Create(trading), bufferedDb,
            NullLogger<MarketDataQualityService>.Instance, Options.Create(Policy),
            Options.Create(new MarketDataCollectionSettings { QualityStateCheckpointMilliseconds = 1000 }), new FixedCheckpointTime());
        using var strict = new MarketDataQualityService(strictDb, Options.Create(trading), strictDb,
            NullLogger<MarketDataQualityService>.Instance, Options.Create(Policy));
        var a = new LatestMarketDataService(Options.Create(Policy), Options.Create(trading));
        var b = new LatestMarketDataService(Options.Create(Policy), Options.Create(trading));
        for (var i = 1; i <= 160; i++)
            foreach (var quote in Pair(Start.AddSeconds(i), Start.AddSeconds(i).AddMilliseconds(20)))
            {
                var x = await buffered.EvaluateAsync(quote); var y = await strict.EvaluateAsync(quote);
                Assert.Equal(y, x); Assert.True(x.IsHealthy);
                a.Apply(quote); b.Apply(quote);
            }
        var end = Start.AddSeconds(161); a.AdvanceTo(end); b.AdvanceTo(end);
        var expected = b.GetCompletedForexAggregates("FX-EURUSD", 5, end);
        Assert.True(expected.Count >= 26);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected),
            System.Text.Json.JsonSerializer.Serialize(a.GetCompletedForexAggregates("FX-EURUSD", 5, end)));
        await buffered.StopAsync(CancellationToken.None);
    }

    private sealed class FixedCheckpointTime : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private static readonly DateTime Start = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ShortAggregateSettings Policy = new() { ForexFutureToleranceMilliseconds = 250 };

    [Theory]
    [InlineData(0, true)]
    [InlineData(300000, true)] // 30 ms
    [InlineData(2500000, true)] // inclusive 250 ms bound
    [InlineData(2500001, false)] // one DateTime tick outside the bound
    [InlineData(15000000, false)]
    public async Task QualityAggregationAndReplayAgreeOnClockBound(long leadTicks, bool accepted)
    {
        using var db = new ForexResearchFixture();
        var trading = ForexCollectionTests.Settings();
        var quality = new MarketDataQualityService(db, Options.Create(trading), db,
            NullLogger<MarketDataQualityService>.Instance, Options.Create(Policy));
        var latest = new LatestMarketDataService(Options.Create(Policy), Options.Create(trading));
        var pair = Pair(Start.AddSeconds(1), Start.AddSeconds(1).AddTicks(-leadTicks));
        foreach (var quote in pair)
        {
            var result = await quality.EvaluateAsync(quote);
            Assert.Equal(accepted ? MarketDataQualityStatus.Healthy : MarketDataQualityStatus.Future, result.Status);
            latest.Apply(quote);
        }
        latest.AdvanceTo(Start.AddSeconds(7));
        Assert.Equal(accepted ? 1 : 0, latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)).Count);
        var rows = Rows(pair);
        if (accepted)
        {
            var replay = Assert.Single(ForexTradeSimulator.ReadQuotes(rows, "FX-EURUSD", "EURUSD", Policy));
            Assert.Equal(pair[0].EventTimeUtc, replay.AvailableUtc);
            Assert.Equal(pair[0].ReceivedTimeUtc, rows[0].ReceivedTimeUtc); // Original receipt is not clamped.
            Assert.Equal(leadTicks > 0 ? 2 : 0, latest.GetForexDiagnostics("FX-EURUSD").ToleratedFutureQuoteEvents);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => ForexTradeSimulator.ReadQuotes(rows, "FX-EURUSD", "EURUSD", Policy));
            Assert.Equal(2, latest.GetForexDiagnostics("FX-EURUSD").FutureQuoteEvents);
        }
    }

    [Fact]
    public void ConservativeReplayAvailabilityCannotHideOutOfOrderEvents()
    {
        var rows = Rows(Pair(Start.AddSeconds(5), Start.AddSeconds(4.8))
            .Concat(Pair(Start.AddSeconds(4.9), Start.AddSeconds(4.9))));
        Assert.Throws<InvalidDataException>(() => ForexTradeSimulator.ReadQuotes(rows, "FX-EURUSD", "EURUSD", Policy));
    }

    [Fact]
    public void ToleranceDoesNotMakeFutureWindowsAvailableEarlyOrAcceptStaleAndCrossedPairs()
    {
        var latest = new LatestMarketDataService(Options.Create(Policy), Options.Create(ForexCollectionTests.Settings()));
        foreach (var quote in Pair(Start.AddSeconds(5), Start.AddSeconds(4.75))) latest.Apply(quote);
        latest.AdvanceTo(Start.AddSeconds(7));
        Assert.Empty(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(7)));
        latest.AdvanceTo(Start.AddSeconds(12));
        var bar = Assert.Single(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(12)));
        Assert.Equal(Start.AddSeconds(5), bar.WindowStartUtc);
        Assert.Equal(Start.AddSeconds(12), bar.AvailableAtUtc);
        foreach (var quote in Pair(Start.AddSeconds(11), Start.AddSeconds(13.01))) latest.Apply(quote);
        foreach (var quote in Pair(Start.AddSeconds(16), Start.AddSeconds(15.9)))
            latest.Apply(quote with { Price = quote.Kind == MarketDataEventKind.Bid ? 2m : 1m });
        latest.AdvanceTo(Start.AddSeconds(22));
        Assert.Single(latest.GetCompletedForexAggregates("FX-EURUSD", 5, Start.AddSeconds(22)));
        Assert.Equal(2, latest.GetForexDiagnostics("FX-EURUSD").StaleQuoteEvents);
    }

    [Fact]
    public void AuditSeparatesObservedSkewFromRejectedSkewAndVersionsThePolicy()
    {
        var rows = Rows(Pair(Start.AddSeconds(1), Start.AddSeconds(.9)));
        ForexCollectionAuditReport Audit(ShortAggregateSettings settings) => ForexCollectionAudit.Analyze(rows,
            ForexCollectionTests.Settings(), settings, new(), "FX-EURUSD", Start, Start.AddSeconds(17));
        var strict = Audit(new()); var tolerant = Audit(Policy);
        Assert.Equal(2, strict.BeyondFutureToleranceQuoteEvents);
        Assert.Equal(2, tolerant.FutureQuoteEvents); // Raw observation remains visible.
        Assert.Equal(0, tolerant.BeyondFutureToleranceQuoteEvents);
        Assert.Equal(2, tolerant.Diagnostics.ToleratedFutureQuoteEvents);
        Assert.Equal(250, tolerant.FutureToleranceMilliseconds);
        Assert.NotEqual(strict.AggregateVersion, tolerant.AggregateVersion);
        Assert.Equal(1, tolerant.Intervals.Single(x => x.IntervalSeconds == 5).FinalBars);
        using var fixture = new ForexResearchFixture();
        var before = fixture.Contract().Get("EURUSD", "patterns");
        fixture.Aggregates.ForexFutureToleranceMilliseconds = 250;
        var after = fixture.Contract().Get("EURUSD", "patterns");
        Assert.NotEqual(before.Feature, after.Feature); Assert.NotEqual(before.Label, after.Label);
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, true)] [InlineData(250, true)] [InlineData(500, true)] [InlineData(501, false)]
    public void ConfigurationHasExplicitSafetyBound(int milliseconds, bool valid)
    {
        var settings = new ShortAggregateSettings { ForexFutureToleranceMilliseconds = milliseconds };
        Assert.Equal(valid, settings.GetValidationErrors().Count == 0);
        if (!valid) Assert.Throws<ArgumentOutOfRangeException>(() => ForexQuoteTimePolicy.IsTooFarAhead(Start, Start, settings));
    }

    private static CanonicalMarketDataEvent[] Pair(DateTime at, DateTime received) =>
        new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask }.Select(kind => new CanonicalMarketDataEvent
        {
            EventId = $"IBKR:FXQ:clock:{at.Ticks}:{kind.ToString().ToLowerInvariant()}",
            InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Kind = kind, EventTimeUtc = at,
            ReceivedTimeUtc = received, Source = ForexQuoteAggregator.QuoteSource,
            Price = kind == MarketDataEventKind.Bid ? 1.1m : 1.1002m, IsFinal = true
        }).ToArray();
    private static MarketDatasetRecord[] Rows(IEnumerable<CanonicalMarketDataEvent> quotes) => quotes.Select(x =>
        MarketDatasetRecord.From(x, new(MarketDataQualityStatus.Healthy, "fixture", true, false), PipelineContractVersions.MarketData)).ToArray();
}
