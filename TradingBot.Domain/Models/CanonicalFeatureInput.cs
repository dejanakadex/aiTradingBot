namespace TradingBot.Domain.Models
{
    public sealed class CanonicalFeatureInput
    {
        public IReadOnlyList<Candle> Candles { get; init; } = Array.Empty<Candle>();
        public DateTime AsOfUtc { get; init; }
        public decimal? Bid { get; init; }
        public DateTime? BidTimeUtc { get; init; }
        public decimal? Ask { get; init; }
        public DateTime? AskTimeUtc { get; init; }
        public decimal? LastTrade { get; init; }
        public DateTime? LastTradeTimeUtc { get; init; }
        public decimal? Spread { get; init; }
        public DateTime? SpreadTimeUtc { get; init; }
    }
}
