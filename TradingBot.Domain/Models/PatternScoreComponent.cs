namespace TradingBot.Domain.Models
{
    public sealed record PatternScoreComponent(
        string Code,
        decimal Score,
        decimal Weight,
        string Reason)
    {
        public decimal WeightedScore => Score * Weight;
    }
}
