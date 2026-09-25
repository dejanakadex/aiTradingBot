using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class PatternDetection
    {
        public int Id { get; set; }
        public string PatternKey { get; set; } = string.Empty;
        public Guid SignalId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public PatternType PatternType { get; set; }
        public Timeframe Timeframe { get; set; }
        public TradeDirection Direction { get; set; }
        public string PatternVersion { get; set; } = string.Empty;
        public DateTime DetectedAtUtc { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
