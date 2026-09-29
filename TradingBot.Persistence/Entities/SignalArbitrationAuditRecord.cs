using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class SignalArbitrationAuditRecord
    {
        public long Id { get; set; }
        public Guid ArbitrationId { get; set; }
        public SignalArbitrationStatus? FromStatus { get; set; }
        public SignalArbitrationStatus ToStatus { get; set; }
        public string Reason { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
