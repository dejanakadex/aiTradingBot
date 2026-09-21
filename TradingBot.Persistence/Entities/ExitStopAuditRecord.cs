using System;

namespace TradingBot.Persistence
{
    public sealed class ExitStopAuditRecord
    {
        public int Id { get; set; }
        public int? TradeId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string EntryBrokerOrderId { get; set; } = string.Empty;
        public string BrokerOrderId { get; set; } = string.Empty;
        public decimal? OldStop { get; set; }
        public decimal NewStop { get; set; }
        public string Reason { get; set; } = string.Empty;
        public decimal MarketPrice { get; set; }
        public decimal HighestPriceSinceEntry { get; set; }
        public decimal? CurrentAtr { get; set; }
        public decimal CurrentRMultiple { get; set; }
        public string ExitManagementState { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
