using System;

namespace TradingBot.Domain.Models
{
    public sealed class PatternGateRejectionSnapshot
    {
        public DateTime TimestampUtc { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string Pattern { get; init; } = string.Empty;
        public string Timeframe { get; init; } = string.Empty;
        public DateTime? PatternDetectedAtUtc { get; init; }
        public decimal? Quality { get; init; }
        public string Reason { get; init; } = string.Empty;
    }
}
