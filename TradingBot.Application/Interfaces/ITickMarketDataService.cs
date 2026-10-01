using System.Threading.Channels;
using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces;

public interface ITickMarketDataSubscription : IAsyncDisposable
{
    ChannelReader<CanonicalMarketDataEvent> Reader { get; }
    long DroppedEvents { get; }
}

public interface ITickMarketDataService
{
    Task<ITickMarketDataSubscription> SubscribeTicksAsync(
        string instrumentId,
        string symbol,
        CancellationToken cancellationToken = default);
}
