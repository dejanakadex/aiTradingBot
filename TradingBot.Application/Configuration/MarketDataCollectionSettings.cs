namespace TradingBot.Application.Configuration
{
    public sealed class MarketDataCollectionSettings
    {
        public bool Enabled { get; set; } = true;
        public bool AutoGapFillEnabled { get; set; } = true;
        public bool RegularSessionOnly { get; set; } = true;
        public int MonitorIntervalSeconds { get; set; } = 5;
        public int HeartbeatGraceSeconds { get; set; } = 30;
        public decimal HeartbeatIntervalMultiplier { get; set; } = 2m;
        public int ReconnectInitialDelaySeconds { get; set; } = 2;
        public int ReconnectMaximumDelaySeconds { get; set; } = 60;
        public int MaximumGapFillLookbackMinutes { get; set; } = 1440;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (MonitorIntervalSeconds <= 0) errors.Add("MarketDataCollection:MonitorIntervalSeconds must be greater than zero.");
            if (HeartbeatGraceSeconds < 0) errors.Add("MarketDataCollection:HeartbeatGraceSeconds cannot be negative.");
            if (HeartbeatIntervalMultiplier < 1m) errors.Add("MarketDataCollection:HeartbeatIntervalMultiplier must be at least 1.");
            if (ReconnectInitialDelaySeconds <= 0) errors.Add("MarketDataCollection:ReconnectInitialDelaySeconds must be greater than zero.");
            if (ReconnectMaximumDelaySeconds < ReconnectInitialDelaySeconds) errors.Add("MarketDataCollection:ReconnectMaximumDelaySeconds cannot be less than ReconnectInitialDelaySeconds.");
            if (MaximumGapFillLookbackMinutes <= 0) errors.Add("MarketDataCollection:MaximumGapFillLookbackMinutes must be greater than zero.");
            return errors;
        }
    }
}
