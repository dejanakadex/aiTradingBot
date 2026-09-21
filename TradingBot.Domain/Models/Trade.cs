using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class Trade
    {
        public string Symbol { get; }
        public decimal Quantity { get; }
        public decimal EntryPrice { get; }
        public decimal? ExitPrice { get; private set; }
        public DateTime OpenedAtUtc { get; }
        public DateTime? ClosedAtUtc { get; private set; }
        public TradeStatus Status { get; private set; }

        public Trade(string symbol, decimal quantity, decimal entryPrice, DateTime openedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (quantity == 0m) throw new ArgumentException("quantity cannot be zero", nameof(quantity));
            if (entryPrice < 0m) throw new ArgumentOutOfRangeException(nameof(entryPrice));

            Symbol = symbol;
            Quantity = quantity;
            EntryPrice = entryPrice;
            OpenedAtUtc = openedAtUtc.Kind == DateTimeKind.Utc ? openedAtUtc : openedAtUtc.ToUniversalTime();
            Status = TradeStatus.Open;
        }

        public void Close(decimal exitPrice, DateTime closedAtUtc)
        {
            if (exitPrice < 0m) throw new ArgumentOutOfRangeException(nameof(exitPrice));
            ExitPrice = exitPrice;
            ClosedAtUtc = closedAtUtc.Kind == DateTimeKind.Utc ? closedAtUtc : closedAtUtc.ToUniversalTime();
            Status = TradeStatus.Closed;
        }
    }
}
