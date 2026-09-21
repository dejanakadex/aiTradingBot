using System;

namespace TradingBot.Persistence
{
    public class Trade
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public DateTime OpenedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public decimal Size { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal? ExitPrice { get; set; }
        public string Notes { get; set; } = string.Empty;
    }
}
