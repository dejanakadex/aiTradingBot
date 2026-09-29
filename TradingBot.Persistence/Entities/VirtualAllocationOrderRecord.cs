namespace TradingBot.Persistence
{
    public sealed class VirtualAllocationOrderRecord
    {
        public long Id { get; set; }
        public Guid ArbitrationId { get; set; }
        public string BrokerOrderId { get; set; } = string.Empty;
        public bool IsEntry { get; set; }
        public decimal RequestedQuantity { get; set; }
        public decimal FilledQuantity { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
