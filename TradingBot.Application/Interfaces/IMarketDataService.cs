using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDataSubscription : IAsyncDisposable
    {
        ChannelReader<MarketBar> Reader { get; }
    }

    public interface IMarketDataService
    {
        Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(string symbol, string timeframe, int count, CancellationToken cancellationToken = default);

        /// <summary>
        /// Subscribe to streaming bars for a symbol/timeframe. Returns a subscription which exposes a ChannelReader for consumption.
        /// Caller should dispose the subscription to unsubscribe.
        /// </summary>
        Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default);
    }
}
