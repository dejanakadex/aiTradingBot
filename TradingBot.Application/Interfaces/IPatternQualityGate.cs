using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPatternQualityGate
    {
        bool TryCreateTradeSetup(
            PatternCandidate pattern,
            MarketSnapshot snapshot,
            out TradeSetupCandidate setupCandidate,
            out IReadOnlyList<string> rejectionReasons);
    }
}
