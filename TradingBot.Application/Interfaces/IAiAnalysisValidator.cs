using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IAiAnalysisValidator
    {
        bool TryValidate(MarketSnapshot snapshot, AiMarketAnalysisResult analysis, out string reason);
    }
}
