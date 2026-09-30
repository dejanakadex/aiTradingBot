using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class InstrumentRolloutEvaluationRecord
    {
        public long Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public InstrumentOnboardingStatus StatusBefore { get; set; }
        public InstrumentOnboardingStatus StatusAfter { get; set; }
        public DateTime EvaluatedAtUtc { get; set; }
        public DateTime WindowStartUtc { get; set; }
        public int ShadowDecisionCount { get; set; }
        public int ApprovedShadowDecisionCount { get; set; }
        public int PaperOrderCount { get; set; }
        public int FilledPaperOrderCount { get; set; }
        public decimal PaperUnfilledRatio { get; set; }
        public decimal? AverageEntrySlippageBps { get; set; }
        public double? AverageFillLatencyMilliseconds { get; set; }
        public int QualityIncidentCount { get; set; }
        public int UnhealthyStreamCount { get; set; }
        public int UnknownOrderCount { get; set; }
        public bool ShadowCriteriaPassed { get; set; }
        public bool PaperCriteriaPassed { get; set; }
        public bool EligibleForManualLiveApproval { get; set; }
        public bool Suspended { get; set; }
        public string ReasonsJson { get; set; } = "[]";
    }
}
