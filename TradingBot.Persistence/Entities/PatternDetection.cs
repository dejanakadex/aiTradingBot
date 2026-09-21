using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class PatternDetection
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public PatternType PatternType { get; set; }
        public DateTime DetectedAtUtc { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
