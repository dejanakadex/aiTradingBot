using System;

namespace TradingBot.Application.DTOs
{
    public sealed class OrderStatusDto
    {
        public string OrderId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? BrokerOrderId { get; init; }
        public DateTime TimestampUtc { get; init; }
        public decimal FilledQuantity { get; init; }
        public decimal RemainingQuantity { get; init; }
        public decimal? AverageFillPrice { get; init; }
        public decimal? LastFillPrice { get; init; }
        public decimal? Commission { get; init; }
        public string? BrokerExecutionId { get; init; }
        public string? Message { get; init; }
    }
}
