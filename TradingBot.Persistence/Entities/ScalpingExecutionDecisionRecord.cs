namespace TradingBot.Persistence
{
    public sealed class ScalpingExecutionDecisionRecord
    {
        public long Id { get; set; }
        public Guid SignalId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string StrategyId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public DateTime EvaluatedAtUtc { get; set; }
        public DateTime? QuoteAsOfUtc { get; set; }
        public bool Approved { get; set; }
        public string SelectedPolicy { get; set; } = string.Empty;
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Quantity { get; set; }
        public int ExpectedHoldingSeconds { get; set; }
        public decimal ExpectedGrossEdgeBps { get; set; }
        public decimal EstimatedCostBps { get; set; }
        public decimal ExpectedNetEdgeBps { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string DecisionJson { get; set; } = string.Empty;
    }
}
