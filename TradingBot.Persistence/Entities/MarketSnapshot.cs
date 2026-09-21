using System;

namespace TradingBot.Persistence
{
    public class MarketSnapshot
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public string RawJson { get; set; } = string.Empty; // placeholder for serialized market data
    }
}
