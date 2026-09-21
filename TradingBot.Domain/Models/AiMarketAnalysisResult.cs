using System;

namespace TradingBot.Domain.Models
{
    public sealed class AiMarketAnalysisResult
    {
        public string Action { get; init; } = AiMarketActions.Reject;
        public decimal Confidence { get; init; }
        public decimal PatternQuality { get; init; }
        public string MarketRegime { get; init; } = string.Empty;
        public decimal ExpectedMovePercent { get; init; }
        public int ExpectedHorizonMinutes { get; init; }
        public decimal EntryMin { get; init; }
        public decimal EntryMax { get; init; }
        public decimal InvalidationPrice { get; init; }
        public string Reason { get; init; } = string.Empty;
        public DateTime AnalyzedAtUtc { get; init; } = DateTime.UtcNow;
        public string Model { get; init; } = string.Empty;
        public bool IsSafeFallback { get; init; }

        public static AiMarketAnalysisResult NoTrade(string reason, string model = "")
        {
            return new AiMarketAnalysisResult
            {
                Action = AiMarketActions.Reject,
                Reason = reason,
                Model = model,
                IsSafeFallback = true
            };
        }
    }

    public static class AiMarketActions
    {
        public const string Buy = "BUY";
        public const string Wait = "WAIT";
        public const string Reject = "REJECT";
    }
}
