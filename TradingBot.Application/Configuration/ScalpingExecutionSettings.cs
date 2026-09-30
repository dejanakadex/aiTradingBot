using TradingBot.Domain.Enums;

namespace TradingBot.Application.Configuration
{
    public sealed class ScalpingExecutionSettings
    {
        public bool Enabled { get; set; } = true;
        public ScalpingOrderPolicy EntryPolicy { get; set; } = ScalpingOrderPolicy.MarketableLimit;
        public int MaximumQuoteAgeMilliseconds { get; set; } = 1500;
        public int MaximumBidAskSkewMilliseconds { get; set; } = 500;
        public int MaximumApprovedPlanAgeMilliseconds { get; set; } = 3000;
        public int MaximumRiskDecisionAgeMilliseconds { get; set; } = 5000;
        public decimal MaximumSpreadBps { get; set; } = 8m;
        public decimal MarketableLimitOffsetBps { get; set; } = 1m;
        public decimal CommissionPerSideBps { get; set; } = 0.5m;
        public decimal PassiveLimitSlippagePerSideBps { get; set; } = 0.25m;
        public decimal MarketableLimitSlippagePerSideBps { get; set; } = 0.75m;
        public decimal MarketSlippagePerSideBps { get; set; } = 1.5m;
        public decimal SafetyBufferBps { get; set; } = 2m;
        public decimal MinimumNetEdgeBps { get; set; } = 3m;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (!Enum.IsDefined(EntryPolicy)) errors.Add("EntryPolicy is unsupported.");
            if (MaximumQuoteAgeMilliseconds <= 0) errors.Add("MaximumQuoteAgeMilliseconds must be greater than zero.");
            if (MaximumBidAskSkewMilliseconds < 0) errors.Add("MaximumBidAskSkewMilliseconds cannot be negative.");
            if (MaximumApprovedPlanAgeMilliseconds <= 0) errors.Add("MaximumApprovedPlanAgeMilliseconds must be greater than zero.");
            if (MaximumRiskDecisionAgeMilliseconds <= 0) errors.Add("MaximumRiskDecisionAgeMilliseconds must be greater than zero.");
            if (MaximumSpreadBps < 0m) errors.Add("MaximumSpreadBps cannot be negative.");
            if (MarketableLimitOffsetBps < 0m) errors.Add("MarketableLimitOffsetBps cannot be negative.");
            if (CommissionPerSideBps < 0m) errors.Add("CommissionPerSideBps cannot be negative.");
            if (PassiveLimitSlippagePerSideBps < 0m) errors.Add("PassiveLimitSlippagePerSideBps cannot be negative.");
            if (MarketableLimitSlippagePerSideBps < 0m) errors.Add("MarketableLimitSlippagePerSideBps cannot be negative.");
            if (MarketSlippagePerSideBps < 0m) errors.Add("MarketSlippagePerSideBps cannot be negative.");
            if (SafetyBufferBps < 0m) errors.Add("SafetyBufferBps cannot be negative.");
            if (MinimumNetEdgeBps < 0m) errors.Add("MinimumNetEdgeBps cannot be negative.");
            return errors;
        }
    }
}
