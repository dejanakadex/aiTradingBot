using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class PortfolioRiskReservationAuditRecord
    {
        public long Id { get; set; }
        public Guid ReservationId { get; set; }
        public PortfolioReservationStatus? FromStatus { get; set; }
        public PortfolioReservationStatus ToStatus { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
