using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class ExitManagementRecord
    {
        public int Id { get; set; }
        public int? TradeId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string EntryBrokerOrderId { get; set; } = string.Empty;
        public string ProtectiveStopBrokerOrderId { get; set; } = string.Empty;
        public ExitManagementState State { get; set; } = ExitManagementState.WaitingForEntryFill;
        public decimal InitialEntryPrice { get; set; }
        public decimal InitialStopPrice { get; set; }
        public decimal InitialRiskPerShare { get; set; }
        public decimal FilledQuantity { get; set; }
        public decimal ProtectedQuantity { get; set; }
        public decimal HighestPriceSinceEntry { get; set; }
        public decimal CurrentProtectiveStop { get; set; }
        public bool BreakEvenActivated { get; set; }
        public bool TrailingActivated { get; set; }
        public string TrailingAtrTimeframe { get; set; } = string.Empty;
        public decimal TrailingAtrMultiplier { get; set; }
        public DateTime OpenedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }
}
