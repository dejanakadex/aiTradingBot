using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class Position
    {
        public string Symbol { get; }
        public decimal Quantity { get; private set; }
        public decimal AveragePrice { get; private set; }
        public TradeStatus Status { get; private set; }

        public Position(string symbol, decimal quantity, decimal averagePrice)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (quantity == 0m) throw new ArgumentException("quantity cannot be zero", nameof(quantity));
            if (averagePrice < 0m) throw new ArgumentOutOfRangeException(nameof(averagePrice));

            Symbol = symbol;
            Quantity = quantity;
            AveragePrice = averagePrice;
            Status = TradeStatus.Open;
        }

        public void Close() => Status = TradeStatus.Closed;

        public void Reduce(decimal amount)
        {
            if (amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));
            if (amount > Math.Abs(Quantity)) throw new ArgumentException("reduction exceeds position size", nameof(amount));
            Quantity = Quantity > 0 ? Quantity - amount : Quantity + amount;
            if (Quantity == 0m) Status = TradeStatus.Closed;
        }
    }
}
