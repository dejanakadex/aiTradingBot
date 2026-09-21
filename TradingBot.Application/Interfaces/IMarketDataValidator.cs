using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDataValidator
    {
        bool TryValidate(MarketBar bar, out string reason);
    }
}
