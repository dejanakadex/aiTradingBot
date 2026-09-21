using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class OrderRequest
    {
        public string Symbol { get; }
        public OrderSide Side { get; }
        public OrderType Type { get; }
        public decimal Quantity { get; }
        public decimal? Price { get; }
        public DateTime RequestedAtUtc { get; }

        public OrderRequest(string symbol, OrderSide side, OrderType type, decimal quantity, decimal? price, DateTime requestedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
            if ((type == OrderType.Limit || type == OrderType.StopLimit) && (!price.HasValue || price <= 0m)) throw new ArgumentException("limit/stoplimit orders require a positive price", nameof(price));

            Symbol = symbol;
            Side = side;
            Type = type;
            Quantity = quantity;
            Price = price;
            RequestedAtUtc = requestedAtUtc.Kind == DateTimeKind.Utc ? requestedAtUtc : requestedAtUtc.ToUniversalTime();
        }
    }
}
