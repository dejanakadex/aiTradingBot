namespace TradingBot.Application.Configuration
{
    public class RiskSettings
    {
        public decimal MaximumPositionValue { get; set; } = 1000m;
        public decimal MaximumRiskPerTrade { get; set; } = 100m;
        public decimal MaximumDailyLoss { get; set; } = 1000m;
        public decimal MaximumLeverage { get; set; } = 1m;
        public int MaximumOpenPositions { get; set; } = 3;
        public int MaximumConsecutiveLosses { get; set; } = 3;
        public int MaximumTradesPerDay { get; set; } = 5;
        public int LossCooldownAfterConsecutiveLosses { get; set; } = 3;
        public int LossCooldownDurationMinutes { get; set; } = 30;
    }
}
