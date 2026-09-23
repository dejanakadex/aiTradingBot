using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class Candle
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public Timeframe Timeframe { get; set; }
        public DateTime TimestampUtc { get; set; }
        public DateTime ReceivedTimeUtc { get; set; }
        public string Source { get; set; } = string.Empty;
        public bool IsFinal { get; set; } = true;
        public MarketDataQualityStatus QualityStatus { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
    }
}
