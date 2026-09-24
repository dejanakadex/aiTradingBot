using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDatasetSink
    {
        ValueTask<bool> EnqueueAsync(MarketDatasetRecord record, CancellationToken cancellationToken = default);
    }
}
