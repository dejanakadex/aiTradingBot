namespace TradingBot.Application.Configuration
{
    using TradingBot.Domain.Enums;

    public class TradingSettings
    {
        public bool Enabled { get; set; } = false;
        public TradingOperatingMode OperatingMode { get; set; } = TradingOperatingMode.AnalysisOnly;
        public bool LiveTradingExplicitlyEnabled { get; set; } = false;
        public string[] Symbols { get; set; } = new[] { "SPY" };
        public string[] MarketDataTimeframes { get; set; } = new[] { "1m", "5m", "15m" };
        public double MinimumPatternQualityForAiAnalysis { get; set; } = 0.6d;
        public decimal MinimumAiConfidence { get; set; } = 0.6m;
        public decimal MinimumPatternQuality { get; set; } = 0.6m;
        public decimal MinimumRewardRiskRatio { get; set; } = 2.0m;
        public decimal MaximumSpread { get; set; } = 0.05m;
        public string[] AllowedMarketRegimes { get; set; } = new[] { "pullback in uptrend", "uptrend", "trending" };
        public int TradingStartHourUtc { get; set; } = 13;
        public int TradingEndHourUtc { get; set; } = 21;
        public int TradingStartHourNewYork { get; set; } = 9;
        public int TradingStartMinuteNewYork { get; set; } = 30;
        public int TradingEndHourNewYork { get; set; } = 16;
        public int TradingEndMinuteNewYork { get; set; } = 0;
        public int MaximumQuoteAgeSeconds { get; set; } = 120;
        public int MaximumCandleAgeSeconds { get; set; } = 300;
        public decimal MaximumAiEntryDeviationPercent { get; set; } = 10m;
        public decimal ExtremeVolatilityPercent { get; set; } = 0.05m;
        public bool RequireProtectiveStopForBotPositions { get; set; } = true;
        public int ProtectiveStopMonitorIntervalSeconds { get; set; } = 15;
    }
}
