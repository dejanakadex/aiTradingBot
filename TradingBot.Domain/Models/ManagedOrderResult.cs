using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class ManagedOrderResult
    {
        public string OrderId { get; init; } = string.Empty;
        public string? BrokerOrderId { get; init; }
        public OrderStatus Status { get; init; }
        public bool Submitted { get; init; }
        public bool IsDuplicate { get; init; }
        public decimal FilledQuantity { get; init; }
        public decimal RemainingQuantity { get; init; }
        public decimal? AverageFillPrice { get; init; }
        public decimal? Commission { get; init; }
        public IReadOnlyList<string> ChildOrderIds { get; init; } = Array.Empty<string>();
        public string Message { get; init; } = string.Empty;
    }
}
