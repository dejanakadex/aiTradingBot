using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IAiMarketAnalyzer
    {
        Task<AiMarketAnalysisResult> AnalyzeAsync(
            MarketSnapshot snapshot,
            PatternCandidate? detectedPattern = null,
            CancellationToken cancellationToken = default);
    }
}
