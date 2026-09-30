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
        public decimal? IndividualFillQuantity { get; init; }
        public decimal RemainingQuantity { get; init; }
        public decimal? AverageFillPrice { get; init; }
        public decimal? LastFillPrice { get; init; }
        public decimal? Commission { get; init; }
        public string? BrokerExecutionId { get; init; }
        public bool IsCommissionUpdate { get; init; }
        public string? Symbol { get; init; }
        public string? Side { get; init; }
        public string? OrderType { get; init; }
        public decimal? RequestedQuantity { get; init; }
        public decimal? StopPrice { get; init; }
        public string? Message { get; init; }
    }
}
