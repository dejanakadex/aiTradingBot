using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class AiMarketAnalysis
    {
        public string ModelVersion { get; }
        public DateTime AnalyzedAtUtc { get; }
        public MarketRegime Regime { get; }
        public decimal Confidence { get; }
        public decimal ExpectedMovePercent { get; }
        public int ExpectedHorizonMinutes { get; }
        public string Summary { get; }

        public AiMarketAnalysis(string modelVersion, DateTime analyzedAtUtc, MarketRegime regime, decimal confidence, decimal expectedMovePercent, int expectedHorizonMinutes, string summary)
        {
            if (string.IsNullOrWhiteSpace(modelVersion)) throw new ArgumentException("modelVersion required", nameof(modelVersion));
            if (confidence < 0m || confidence > 1m) throw new ArgumentOutOfRangeException(nameof(confidence));
            if (expectedHorizonMinutes < 0) throw new ArgumentOutOfRangeException(nameof(expectedHorizonMinutes));

            ModelVersion = modelVersion;
            AnalyzedAtUtc = analyzedAtUtc.Kind == DateTimeKind.Utc ? analyzedAtUtc : analyzedAtUtc.ToUniversalTime();
            Regime = regime;
            Confidence = confidence;
            ExpectedMovePercent = expectedMovePercent;
            ExpectedHorizonMinutes = expectedHorizonMinutes;
            Summary = summary ?? string.Empty;
        }
    }
}
