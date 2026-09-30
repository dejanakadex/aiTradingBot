using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed record ExecutionCostEstimate(
        ScalpingOrderPolicy Policy,
        decimal EntryPrice,
        decimal SpreadBps,
        decimal CommissionBps,
        decimal SlippageBps,
        decimal SafetyBufferBps,
        decimal TotalCostBps,
        decimal ExpectedGrossEdgeBps,
        decimal ExpectedNetEdgeBps);

    public sealed record ScalpingExecutionDecision(
        bool Approved,
        string Reason,
        DateTime EvaluatedAtUtc,
        DateTime QuoteAsOfUtc,
        decimal Bid,
        decimal Ask,
        decimal Quantity,
        int ExpectedHoldingSeconds,
        ScalpingOrderPolicy SelectedPolicy,
        OrderType EntryOrderType,
        decimal? EntryPrice,
        ExecutionCostEstimate SelectedEstimate,
        IReadOnlyList<ExecutionCostEstimate> Estimates,
        IReadOnlyList<string> RejectionReasons)
    {
        public static ScalpingExecutionDecision Reject(DateTime now, string reason, IReadOnlyList<string> reasons) =>
            new(false, reason, now, default, 0m, 0m, 0m, 0, ScalpingOrderPolicy.MarketableLimit,
                OrderType.Limit, null, new ExecutionCostEstimate(ScalpingOrderPolicy.MarketableLimit, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m),
                Array.Empty<ExecutionCostEstimate>(), reasons);
    }
}
