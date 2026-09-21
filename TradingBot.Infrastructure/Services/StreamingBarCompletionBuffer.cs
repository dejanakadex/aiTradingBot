using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Services
{
    public sealed class StreamingBarCompletionBuffer
    {
        private MarketBar? _pending;

        public MarketBar? AcceptUpdate(MarketBar update)
        {
            if (_pending == null)
            {
                _pending = update;
                return null;
            }

            if (IsSameBar(_pending, update))
            {
                _pending = update;
                return null;
            }

            var completed = _pending;
            _pending = update;
            return completed;
        }

        private static bool IsSameBar(MarketBar left, MarketBar right)
        {
            return left.TimestampUtc == right.TimestampUtc
                && left.Timeframe.Equals(right.Timeframe, StringComparison.OrdinalIgnoreCase)
                && left.Symbol.Equals(right.Symbol, StringComparison.OrdinalIgnoreCase);
        }
    }
}
