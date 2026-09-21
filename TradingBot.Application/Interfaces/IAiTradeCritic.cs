using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IAiTradeCritic
    {
        Task<AiTradeCriticResult> CritiqueAsync(
            MarketSnapshot snapshot,
            PatternCandidate pattern,
            AiMarketAnalysisResult analysis,
            CancellationToken cancellationToken = default);
    }
}
