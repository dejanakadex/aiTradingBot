using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class StreamingBarCompletionBufferTests
    {
        [Fact]
        public void AcceptUpdate_BuffersCurrentBarUntilNextTimestamp()
        {
            var buffer = new StreamingBarCompletionBuffer();
            var first = Bar("SPY", "1m", new DateTime(2026, 8, 25, 13, 30, 0, DateTimeKind.Utc), 100m);
            var revisedFirst = Bar("SPY", "1m", first.TimestampUtc, 101m);
            var second = Bar("SPY", "1m", first.TimestampUtc.AddMinutes(1), 102m);

            Assert.Null(buffer.AcceptUpdate(first));
            Assert.Null(buffer.AcceptUpdate(revisedFirst));

            var completed = buffer.AcceptUpdate(second);

            Assert.NotNull(completed);
            Assert.Equal(revisedFirst.TimestampUtc, completed.TimestampUtc);
            Assert.Equal(revisedFirst.Close, completed.Close);
        }

        private static MarketBar Bar(string symbol, string timeframe, DateTime timestampUtc, decimal close)
        {
            return new MarketBar
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = timestampUtc,
                Open = close - 0.5m,
                High = close + 0.5m,
                Low = close - 1m,
                Close = close,
                Volume = 1_000m
            };
        }
    }
}
