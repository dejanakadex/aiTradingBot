using System;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests
{
    public class DomainModelsTests
    {
        [Fact]
        public void Candle_ValidConstruction_SetsProperties()
        {
            var ts = DateTime.UtcNow;
            var c = new Candle("BTCUSD", Timeframe.OneMinute, ts, 1.0m, 2.0m, 0.5m, 1.5m, 100m);

            Assert.Equal("BTCUSD", c.Symbol);
            Assert.Equal(Timeframe.OneMinute, c.Timeframe);
            Assert.Equal(1.0m, c.Open);
            Assert.Equal(2.0m, c.High);
            Assert.Equal(0.5m, c.Low);
            Assert.Equal(1.5m, c.Close);
            Assert.Equal(100m, c.Volume);
            Assert.Equal(ts.ToUniversalTime(), c.TimestampUtc);
        }

        [Fact]
        public void Candle_HighLessThanLow_Throws()
        {
            Assert.Throws<ArgumentException>(() => new Candle("X", Timeframe.OneMinute, DateTime.UtcNow, 1m, 0.5m, 1.0m, 0.75m, 1m));
        }

        [Fact]
        public void Candle_NegativePrice_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Candle("X", Timeframe.OneMinute, DateTime.UtcNow, -1m, 2m, 0m, 0m, 1m));
        }

        [Fact]
        public void PatternCandidate_ConfidenceRange_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PatternCandidate(PatternType.HeadAndShoulders, "SYM", Timeframe.OneHour, DateTime.UtcNow, -0.1m));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PatternCandidate(PatternType.HeadAndShoulders, "SYM", Timeframe.OneHour, DateTime.UtcNow, 1.1m));
        }

        [Fact]
        public void TradeSignal_InvalidQuantityOrConfidence_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TradeSignal("SYM", Timeframe.OneHour, TradeAction.Enter, 0.5m, 0m, DateTime.UtcNow));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TradeSignal("SYM", Timeframe.OneHour, TradeAction.Enter, -0.1m, 1m, DateTime.UtcNow));
        }

        [Fact]
        public void OrderRequest_LimitRequiresPrice_Throws()
        {
            Assert.Throws<ArgumentException>(() => new OrderRequest("SYM", OrderSide.Buy, OrderType.Limit, 1m, null, DateTime.UtcNow));
        }

        [Fact]
        public void Position_Reduce_ClosesWhenZero()
        {
            var p = new Position("SYM", 10m, 100m);
            p.Reduce(10m);
            Assert.Equal(0m, p.Quantity);
            Assert.Equal(TradeStatus.Closed, p.Status);
        }

        [Fact]
        public void Trade_Close_SetsExit()
        {
            var t = new Trade("SYM", 1m, 100m, DateTime.UtcNow);
            t.Close(110m, DateTime.UtcNow);
            Assert.Equal(110m, t.ExitPrice);
            Assert.Equal(TradeStatus.Closed, t.Status);
            Assert.NotNull(t.ClosedAtUtc);
        }
    }
}
