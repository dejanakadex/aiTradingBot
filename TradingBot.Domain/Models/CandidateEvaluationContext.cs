using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed record CandidateEvaluationContext(
        MarketRegime MarketRegime,
        decimal? NormalizedLiquidity,
        decimal? NormalizedVolatility);
}
