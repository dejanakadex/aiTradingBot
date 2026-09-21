using System;
using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class StrategyDecision
    {
        public string Symbol { get; init; } = string.Empty;
        public DateTime EvaluatedAtUtc { get; init; } = DateTime.UtcNow;
        public bool Approved { get; init; }
        public TradeSignal? TradeSignal { get; init; }
        public IReadOnlyList<string> RejectionReasons { get; init; } = Array.Empty<string>();
        public decimal? EntryMin { get; init; }
        public decimal? EntryMax { get; init; }
        public decimal? StopPrice { get; init; }
        public decimal? TakeProfitPrice { get; init; }
        public decimal? ExpectedRewardRiskRatio { get; init; }
    }
}
