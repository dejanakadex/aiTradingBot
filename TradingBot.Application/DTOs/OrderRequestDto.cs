using System;

namespace TradingBot.Application.DTOs
{
    public sealed class OrderRequestDto
    {
        public string Symbol { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public decimal? LimitPrice { get; init; }
        public decimal? StopPrice { get; init; }
        public string Side { get; init; } = string.Empty; // "BUY"/"SELL"
        public string Type { get; init; } = string.Empty; // "MARKET"/"LIMIT"/etc
        public DateTime CreatedAtUtc { get; init; }
        public string? ParentBrokerOrderId { get; init; }
        public bool? Transmit { get; init; }
        public string? Role { get; init; }
    }
}
