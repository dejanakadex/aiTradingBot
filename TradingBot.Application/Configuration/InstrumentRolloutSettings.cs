namespace TradingBot.Application.Configuration
{
    public sealed class InstrumentRolloutSettings
    {
        public bool Enabled { get; set; } = true;
        public int EvaluationIntervalSeconds { get; set; } = 30;
        public int LookbackHours { get; set; } = 24;
        public int MinimumShadowDecisions { get; set; } = 25;
        public int MinimumApprovedShadowDecisions { get; set; } = 5;
        public int MinimumPaperOrders { get; set; } = 20;
        public decimal MaximumPaperUnfilledRatio { get; set; } = 0.20m;
        public decimal MaximumAverageEntrySlippageBps { get; set; } = 3m;
        public int MaximumAverageFillLatencyMilliseconds { get; set; } = 2500;
        public int MaximumRecentQualityIncidents { get; set; } = 0;
        public bool AutoAdvanceToShadow { get; set; } = true;
        public bool AutoAdvanceToPaper { get; set; } = true;
        public bool AutoSuspend { get; set; } = true;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (EvaluationIntervalSeconds < 5) errors.Add("EvaluationIntervalSeconds must be at least 5.");
            if (LookbackHours < 1) errors.Add("LookbackHours must be at least 1.");
            if (MinimumShadowDecisions < 1) errors.Add("MinimumShadowDecisions must be at least 1.");
            if (MinimumApprovedShadowDecisions < 1 || MinimumApprovedShadowDecisions > MinimumShadowDecisions) errors.Add("MinimumApprovedShadowDecisions must be between 1 and MinimumShadowDecisions.");
            if (MinimumPaperOrders < 1) errors.Add("MinimumPaperOrders must be at least 1.");
            if (MaximumPaperUnfilledRatio is < 0m or > 1m) errors.Add("MaximumPaperUnfilledRatio must be between 0 and 1.");
            if (MaximumAverageEntrySlippageBps < 0m) errors.Add("MaximumAverageEntrySlippageBps cannot be negative.");
            if (MaximumAverageFillLatencyMilliseconds < 1) errors.Add("MaximumAverageFillLatencyMilliseconds must be positive.");
            if (MaximumRecentQualityIncidents < 0) errors.Add("MaximumRecentQualityIncidents cannot be negative.");
            return errors;
        }
    }
}
