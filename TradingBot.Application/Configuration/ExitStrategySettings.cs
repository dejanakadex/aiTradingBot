using TradingBot.Domain.Enums;

namespace TradingBot.Application.Configuration
{
    public sealed class ExitStrategySettings
    {
        public ExitStrategyMode Mode { get; set; } = ExitStrategyMode.AtrTrailing;
        public decimal BreakEvenTriggerR { get; set; } = 0.8m;
        public decimal BreakEvenOffsetR { get; set; } = 0.05m;
        public decimal TrailingActivationR { get; set; } = 1.2m;
        public decimal TrailingAtrMultiplier { get; set; } = 1.0m;
        public string TrailingAtrTimeframe { get; set; } = "1m";
        public int? MaximumHoldingMinutes { get; set; } = 30;
    }
}
