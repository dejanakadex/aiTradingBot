using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class TickLatencyWindowTests
{
    private static readonly DateTime Start = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static CanonicalMarketDataEvent Quote(double receiveSeconds) => new()
    {
        EventId = "quote", InstrumentId = "FX-EURUSD", Symbol = "EURUSD", Kind = MarketDataEventKind.Bid,
        EventTimeUtc = Start, ReceivedTimeUtc = Start.AddSeconds(receiveSeconds), Price = 1.1m
    };

    [Fact]
    public void SlowDatabaseProcessingCannotBeMistakenForOldTimestampAtCallback()
    {
        var window = new TickLatencyWindow(); var quote = Quote(.5);
        window.Record(quote, Start.AddSeconds(3), TimeSpan.FromSeconds(4), false);
        var report = window.Take();
        Assert.Equal(0, report.ReceiveAgeOverTwoSeconds);
        Assert.Equal(500d, report.MaxReceiveMinusEventMs);
        Assert.Equal(2500d, report.MaxDequeueMinusReceiptMs);
        Assert.Equal(4000d, report.MaxPipelineMs);
        Assert.Equal(Start.AddSeconds(.5), quote.ReceivedTimeUtc);
        Assert.Equal(Start, quote.EventTimeUtc);
    }

    [Fact]
    public void WorstSourceAgeKeepsItsOwnQueueAndProcessingMeasurements()
    {
        var window = new TickLatencyWindow();
        window.Record(Quote(3), Start.AddSeconds(3).AddMilliseconds(10), TimeSpan.FromMilliseconds(5), false);
        window.Record(Quote(.5), Start.AddSeconds(5.5), TimeSpan.FromSeconds(1), true);
        var report = window.Take();
        Assert.Equal(2, report.Events); Assert.Equal(1, report.FailedProcessing);
        Assert.Equal(1, report.ReceiveAgeOverTwoSeconds);
        Assert.Equal(1750d, report.MeanReceiveMinusEventMs);
        Assert.Equal(5000d, report.MaxDequeueMinusReceiptMs);
        Assert.Equal(Start.AddSeconds(3), report.OldestQuoteReceivedUtc);
        Assert.Equal(10d, report.OldestQuoteDequeueMinusReceiptMs);
        Assert.Equal(5d, report.OldestQuotePipelineMs);
        var empty = window.Take();
        Assert.Equal(0, empty.Events); Assert.Null(empty.MaxReceiveMinusEventMs);
        Assert.Null(empty.OldestQuoteReceivedUtc); Assert.Null(empty.MaxPipelineMs);
        Assert.Equal(2, report.Events); // Published snapshots cannot be changed by reset.
    }

    [Fact]
    public void ClockCorrectionsAndExactlyTwoSecondsRemainExplicit()
    {
        var window = new TickLatencyWindow();
        window.Record(Quote(2), Start.AddSeconds(1.9), TimeSpan.FromMilliseconds(3), false);
        var report = window.Take();
        Assert.Equal(0, report.ReceiveAgeOverTwoSeconds);
        Assert.Equal(1, report.NegativeQueueClockDeltas);
        Assert.Equal(-100d, report.MeanDequeueMinusReceiptMs);
        window.Record(Quote(-.1), Start, TimeSpan.FromMilliseconds(1), false);
        var future = window.Take();
        Assert.Equal(-100d, future.MaxReceiveMinusEventMs);
        Assert.Equal(100d, future.MeanDequeueMinusReceiptMs);
        Assert.Equal(0, future.NegativeQueueClockDeltas);
    }
}
