using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IStrategyEngine
    {
        Task<StrategyDecision> EvaluateAsync(
            MarketSnapshot snapshot,
            PatternCandidate pattern,
            AiMarketAnalysisResult analysis,
            AiTradeCriticResult critic,
            CancellationToken cancellationToken = default);
    }
}
