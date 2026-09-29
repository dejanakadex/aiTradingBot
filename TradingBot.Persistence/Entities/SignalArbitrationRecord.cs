using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class SignalArbitrationRecord
    {
        public Guid Id { get; set; }
        public string ArbitrationKey { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public Guid SignalId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public TradeDirection Direction { get; set; }
        public int Priority { get; set; }
        public decimal Confidence { get; set; }
        public SignalArbitrationStatus Status { get; set; }
        public Guid? ReservationId { get; set; }
        public decimal ApprovedQuantity { get; set; }
        public decimal PositionValue { get; set; }
        public decimal FilledQuantity { get; set; }
        public decimal ExitedQuantity { get; set; }
        public string? EntryBrokerOrderId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Version { get; set; }
    }
}
