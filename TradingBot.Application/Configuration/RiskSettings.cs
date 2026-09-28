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
        public decimal MaximumGrossExposure { get; set; } = 3000m;
        public decimal MaximumNetExposure { get; set; } = 3000m;
        public decimal MaximumInstrumentExposure { get; set; } = 1000m;
        public decimal MaximumStrategyExposure { get; set; } = 2000m;
        public int MaximumPendingReservations { get; set; } = 3;
        public int MaximumPendingReservationsPerInstrument { get; set; } = 1;
        public int MaximumPendingReservationsPerStrategy { get; set; } = 3;
        public int ReservationTimeoutSeconds { get; set; } = 120;
        public int CommittedReservationTimeoutSeconds { get; set; } = 300;
        public int InstrumentCooldownSeconds { get; set; } = 30;
        public decimal MaximumCorrelationGroupExposure { get; set; } = 2000m;
        public Dictionary<string, string[]> CorrelationGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
