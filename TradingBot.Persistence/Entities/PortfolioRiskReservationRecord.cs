using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class PortfolioRiskReservationRecord
    {
        public Guid Id { get; set; }
        public string ReservationKey { get; set; } = string.Empty;
        public string AccountId { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public Guid SignalId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal ReferencePrice { get; set; }
        public decimal PositionValue { get; set; }
        public decimal RiskAmount { get; set; }
        public decimal SignedExposure { get; set; }
        public PortfolioReservationStatus Status { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public DateTime? CommittedAtUtc { get; set; }
        public DateTime? ReleasedAtUtc { get; set; }
        public string? BrokerOrderId { get; set; }
        public string Reason { get; set; } = string.Empty;
        public int Version { get; set; }
    }
}
