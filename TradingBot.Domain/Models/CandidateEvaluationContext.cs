using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed record CandidateEvaluationContext(
        MarketRegime MarketRegime,
        decimal? NormalizedLiquidity,
        decimal? NormalizedVolatility,
        DateTime? AvailableAtUtc = null,
        decimal? Bid = null,
        DateTime? BidTimeUtc = null,
        decimal? Ask = null,
        DateTime? AskTimeUtc = null);
}
