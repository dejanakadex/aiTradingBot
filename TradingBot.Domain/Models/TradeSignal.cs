using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class TradeSignal
    {
        public string Symbol { get; }
        public Timeframe Timeframe { get; }
        public TradeAction Action { get; }
        public decimal Confidence { get; }
        public decimal SuggestedQuantity { get; }
        public DateTime GeneratedAtUtc { get; }

        public TradeSignal(string symbol, Timeframe timeframe, TradeAction action, decimal confidence, decimal suggestedQuantity, DateTime generatedAtUtc)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (confidence < 0m || confidence > 1m) throw new ArgumentOutOfRangeException(nameof(confidence));
            if (suggestedQuantity <= 0m) throw new ArgumentOutOfRangeException(nameof(suggestedQuantity));

            Symbol = symbol;
            Timeframe = timeframe;
            Action = action;
            Confidence = confidence;
            SuggestedQuantity = suggestedQuantity;
            GeneratedAtUtc = generatedAtUtc.Kind == DateTimeKind.Utc ? generatedAtUtc : generatedAtUtc.ToUniversalTime();
        }
    }
}
