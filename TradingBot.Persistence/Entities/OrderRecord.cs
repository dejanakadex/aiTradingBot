using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public class OrderRecord
    {
        public int Id { get; set; }
        public string BrokerOrderId { get; set; } = string.Empty;
        public string ClientOrderKey { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
        public OrderStatus Status { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }
}
