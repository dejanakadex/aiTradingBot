namespace TradingBot.Domain.Models
{
    public sealed record PatternConditionResult(
        string Code,
        bool Passed,
        string Reason);
}
