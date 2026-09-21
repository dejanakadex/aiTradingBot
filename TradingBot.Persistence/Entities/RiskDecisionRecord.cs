using System;

namespace TradingBot.Persistence
{
    public class RiskDecisionRecord
    {
        public int Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string DecisionJson { get; set; } = string.Empty;
    }
}
