using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class OrderRecord
    {
        public int Id { get; set; }
        public string BrokerOrderId { get; set; } = string.Empty;
        public string ClientOrderKey { get; set; } = string.Empty;
        public Guid IntentId { get; set; }
        public string Role { get; set; } = string.Empty;
        public string ParentBrokerOrderId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public OrderStatus Status { get; set; }
        public string Side { get; set; } = string.Empty;
        public string OrderType { get; set; } = string.Empty;
        public decimal RequestedQuantity { get; set; }
        public decimal FilledQuantity { get; set; }
        public decimal RemainingQuantity { get; set; }
        public decimal? LimitPrice { get; set; }
        public decimal? StopPrice { get; set; }
        public decimal? AverageFillPrice { get; set; }
        public decimal TotalCommission { get; set; }
        public DateTime? CancelRequestedUtc { get; set; }
        public DateTime? CancelConfirmedUtc { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }
}
