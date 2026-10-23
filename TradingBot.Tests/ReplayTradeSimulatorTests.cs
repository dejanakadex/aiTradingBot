using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ReplayTradeSimulatorTests
{
    private static readonly DateTime Start = new(2026, 10, 6, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PartialEntryClosesAtTargetWithMinimumCommissionsAndSeparateHorizonReturn()
    {
        var events = Seed();
        events.Add(Tick("ask-2", "ask", 2, 100.02m, 20m));
        events.Add(Tick("ask-3", "ask", 3, 100.02m, 20m));
        events.Add(Tick("bid-target", "bid", 4, 100.60m, 40m));
        events.Add(Tick("bid-horizon", "bid", 63, 100.70m, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal("Target", trade.ExitReason);
        Assert.Equal(9m, trade.RequestedQuantity);
        Assert.Equal(9m, trade.FilledQuantity);
        Assert.Equal(9m, trade.ExitedQuantity);
        Assert.Equal(2m, trade.Commission);
        Assert.Equal(trade.GrossPnl - 2m, trade.NetPnl);
        Assert.True(trade.HorizonReturnBps > 0m);
        Assert.Equal(Start.AddSeconds(4), trade.ExitTimeUtc);
    }

    [Fact]
    public void PassiveLimitTouchDoesNotFill()
    {
        var events = Seed();
        events.Add(Tick("touch", "trade", 2, 100.00m, 100m));
        events.Add(Tick("late", "trade", 12, 99.99m, 100m));

        var trade = Assert.Single(Simulator(ScalpingOrderPolicy.PassiveLimit)
            .Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Unfilled", trade.Status);
        Assert.Equal(0m, trade.FilledQuantity);
        Assert.Null(trade.NetPnl);
    }

    [Fact]
    public void PassiveFillUsesPostedLimitRatherThanMoreFavorableTradePrint()
    {
        var events = Seed();
        events.Add(Tick("trade-through", "trade", 2, 99.90m, 40m));
        events.Add(Tick("bid-target", "bid", 3, 100.60m, 40m));

        var trade = Assert.Single(Simulator(ScalpingOrderPolicy.PassiveLimit)
            .Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal(100m, trade.EntryPrice);
    }

    [Fact]
    public void TimerCancelsUnfilledEntryRemainderBeforeAnotherAskCanFill()
    {
        var events = Seed();
        events.Add(Tick("first-partial", "ask", 2, 100.02m, 20m));
        events.Add(Tick("ask-after-deadline", "ask", 5, 100.02m, 40m));
        events.Add(Tick("bid-timer-exit", "bid", 6, 100.05m, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()],
            new ReplayBacktestRequest(HoldingSeconds: 2)));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal("Timer", trade.ExitReason);
        Assert.Equal(5m, trade.FilledQuantity);
        Assert.Equal(5m, trade.ExitedQuantity);
    }

    [Fact]
    public void TriggeredStopWithoutBidLiquidityStillCancelsEntryAndExitsLater()
    {
        var events = Seed();
        events.Add(Tick("first-partial", "ask", 2, 100.02m, 20m));
        events.Add(Tick("stop-without-size", "bid", 3, 99.80m, 0m));
        events.Add(Tick("ask-after-stop", "ask", 4, 100.02m, 40m));
        events.Add(Tick("bid-rebound", "bid", 5, 100.10m, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal("Stop", trade.ExitReason);
        Assert.Equal(5m, trade.FilledQuantity);
        Assert.Equal(5m, trade.ExitedQuantity);
        Assert.Equal(Start.AddSeconds(5), trade.ExitTimeUtc);
    }

    [Fact]
    public void TargetWithoutBidLiquidityCancelsEntryButRemainsALimitExit()
    {
        var events = Seed();
        events.Add(Tick("first-partial", "ask", 2, 100.02m, 20m));
        events.Add(Tick("target-without-size", "bid", 3, 100.60m, 0m));
        events.Add(Tick("ask-after-target", "ask", 4, 100.02m, 40m));
        events.Add(Tick("bid-below-target", "bid", 5, 100.30m, 40m));
        events.Add(Tick("bid-target-fill", "bid", 6, 100.60m, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal("Target", trade.ExitReason);
        Assert.Equal(5m, trade.FilledQuantity);
        Assert.Equal(5m, trade.ExitedQuantity);
        Assert.Equal(Start.AddSeconds(6), trade.ExitTimeUtc);
    }

    [Theory]
    [InlineData(3, 99.80, "Stop")]
    [InlineData(63, 100.05, "Timer")]
    public void StopAndTimerExitUseEventTime(int exitSecond, double bidPrice, string expectedReason)
    {
        var events = Seed();
        events.Add(Tick("ask-fill", "ask", 2, 100.02m, 40m));
        events.Add(Tick("bid-exit", "bid", exitSecond, (decimal)bidPrice, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal(expectedReason, trade.ExitReason);
        Assert.Equal(Start.AddSeconds(exitSecond), trade.ExitTimeUtc);
        Assert.True(expectedReason == "Stop" ? trade.NetPnl < 0m : trade.NetPnl.HasValue);
    }

    [Fact]
    public void MissingExitLiquidityLeavesPositionOpenWithoutInventedPnl()
    {
        var events = Seed();
        events.Add(Tick("ask-fill", "ask", 2, 100.02m, 40m));
        events.Add(Tick("bid-no-size", "bid", 63, 100.40m, 0m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("OpenAtDatasetEnd", trade.Status);
        Assert.Null(trade.NetPnl);
        Assert.NotNull(trade.HorizonReturnBps);
    }

    [Fact]
    public void TargetThatCannotCoverMinimumCommissionIsRejectedBeforeOrder()
    {
        var trade = Assert.Single(Simulator().Simulate(Seed(), [Signal()],
            new ReplayBacktestRequest(TargetBps: 5m)));

        Assert.Equal("InsufficientNetEdge", trade.Status);
        Assert.Equal(0m, trade.FilledQuantity);
    }

    [Fact]
    public void PartialTargetDoesNotFillRemainingQuantityBelowLimitAndStopCanFinishTrade()
    {
        var events = Seed();
        events.Add(Tick("ask-fill", "ask", 2, 100.02m, 40m));
        events.Add(Tick("bid-target-partial", "bid", 3, 100.60m, 20m));
        events.Add(Tick("bid-under-target", "bid", 4, 100.20m, 40m));
        events.Add(Tick("bid-stop", "bid", 5, 99.80m, 40m));

        var trade = Assert.Single(Simulator().Simulate(events, [Signal()], new ReplayBacktestRequest()));

        Assert.Equal("Closed", trade.Status);
        Assert.Equal("TargetThenStop", trade.ExitReason);
        Assert.Equal(Start.AddSeconds(5), trade.ExitTimeUtc);
        Assert.Equal(9m, trade.ExitedQuantity);
    }

    [Fact]
    public void LaterSignalCannotUseQuotesSeenWhileFinishingEarlierTrade()
    {
        var events = Seed();
        events.Add(Tick("ask-fill", "ask", 2, 100.02m, 40m));
        events.Add(Tick("bid-target", "bid", 4, 100.60m, 40m));
        events.Add(new MarketDatasetRecord { EventId = "signal-2", DataType = "bar",
            EventTimeUtc = Start.AddSeconds(5), IsFinal = true, CanTriggerTrading = true,
            QualityStatus = "Healthy" });
        events.Add(Tick("future-bid", "bid", 63, 100.70m, 40m));
        events.Add(Tick("future-ask", "ask", 63, 100.72m, 40m));
        var later = Signal() with { SignalId = Guid.Parse("02020202-0202-0202-0202-020202020202"),
            SourceEventId = "signal-2", DetectedAtUtc = Start.AddSeconds(5) };

        var trades = Simulator().Simulate(events, [Signal(), later], new ReplayBacktestRequest());

        Assert.Equal("Closed", trades[0].Status);
        Assert.Equal("NoExecutableQuote", trades[1].Status);
    }

    [Fact]
    public void SignalsUseAvailabilityOrderAndQuotesAsOfTheirDecisionTime()
    {
        var events = Seed();
        events.Add(Tick("early-bid", "bid", 4, 100m, 40m));
        events.Add(Tick("early-ask", "ask", 4, 100.02m, 40m));
        events.Add(new MarketDatasetRecord { EventId = "second-bar", DataType = "bar",
            EventTimeUtc = Start.AddSeconds(5), IsFinal = true, CanTriggerTrading = true,
            QualityStatus = "Healthy" });
        events.Add(Tick("early-fill", "ask", 6, 100.02m, 40m));
        events.Add(Tick("early-target", "bid", 7, 100.60m, 40m));
        events.Add(Tick("late-bid", "bid", 9, 100m, 40m));
        events.Add(Tick("late-ask", "ask", 9, 100.02m, 40m));
        events.Add(Tick("late-fill", "ask", 11, 100.02m, 40m));
        events.Add(Tick("late-target", "bid", 12, 100.60m, 40m));
        var timerSignal = Signal() with
        {
            MetadataJson = $"{{\"{ReplayTradeSimulator.AvailableAtMetadataKey}\":\"{Start.AddSeconds(10):O}\"}}"
        };
        var earlierDecision = Signal() with
        {
            SignalId = Guid.Parse("02020202-0202-0202-0202-020202020202"),
            SourceEventId = "second-bar",
            DetectedAtUtc = Start.AddSeconds(5)
        };

        var trades = Simulator().Simulate(events, [timerSignal, earlierDecision], new ReplayBacktestRequest());

        Assert.Collection(trades,
            early => { Assert.Equal(earlierDecision.SignalId, early.SignalId); Assert.Equal("Closed", early.Status); },
            late => { Assert.Equal(timerSignal.SignalId, late.SignalId); Assert.Equal("Closed", late.Status); });
        Assert.Equal(Start.AddSeconds(6), trades[0].EntryTimeUtc);
        Assert.Equal(Start.AddSeconds(11), trades[1].EntryTimeUtc);
    }

    private static ReplayTradeSimulator Simulator(ScalpingOrderPolicy policy = ScalpingOrderPolicy.MarketableLimit) => new(
        new PositionSizer(Options.Create(new RiskSettings())),
        Options.Create(new RiskSettings()),
        Options.Create(new ScalpingExecutionSettings { EntryPolicy = policy }));

    private static ReplaySignalSnapshot Signal() => new(0, Guid.Empty, Guid.Parse("01010101-0101-0101-0101-010101010101"),
        "signal-bar", PatternType.Hammer, TradeDirection.Long, "SPY", Timeframe.OneMinute,
        Start.AddSeconds(1), 0.9m, "{}", "{}", "{}");

    private static List<MarketDatasetRecord> Seed() =>
    [
        Tick("bid-decision", "bid", 0, 100.00m, 100m),
        Tick("ask-decision", "ask", 0, 100.02m, 100m),
        new MarketDatasetRecord { EventId = "signal-bar", DataType = "bar", EventTimeUtc = Start.AddSeconds(1),
            IsFinal = true, CanTriggerTrading = true, QualityStatus = "Healthy", Close = 100.01m }
    ];

    private static MarketDatasetRecord Tick(string id, string kind, int second, decimal price, decimal size) => new()
    {
        EventId = id, DataType = kind, EventTimeUtc = Start.AddSeconds(second), Price = price, Size = size,
        IsFinal = true, CanTriggerTrading = true, QualityStatus = "Healthy"
    };
}
