using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class PatternCandidate
    {
        public PatternType PatternType { get; }
        public string Symbol { get; }
        public Timeframe Timeframe { get; }
        public DateTime DetectedAtUtc { get; }
        public decimal Confidence { get; }
        public IReadOnlyList<decimal> RelevantPriceLevels { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public PatternCandidate(PatternType patternType, string symbol, Timeframe timeframe, DateTime detectedAtUtc, decimal confidence,
            IEnumerable<decimal>? relevantPriceLevels = null, IDictionary<string, string>? metadata = null)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (confidence < 0m || confidence > 1m) throw new ArgumentOutOfRangeException(nameof(confidence), "confidence must be between 0 and 1");

            PatternType = patternType;
            Symbol = symbol;
            Timeframe = timeframe;
            DetectedAtUtc = detectedAtUtc.Kind switch
            {
                DateTimeKind.Utc => detectedAtUtc,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(detectedAtUtc, DateTimeKind.Utc),
                _ => detectedAtUtc.ToUniversalTime()
            };
            Confidence = confidence;
            RelevantPriceLevels = new List<decimal>(relevantPriceLevels ?? Array.Empty<decimal>());
            Metadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>());
        }
    }
}
