using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record InstrumentRolloutEvaluation
    {
        public long Id { get; init; }
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public InstrumentOnboardingStatus StatusBefore { get; init; }
        public InstrumentOnboardingStatus StatusAfter { get; init; }
        public DateTime EvaluatedAtUtc { get; init; }
        public DateTime WindowStartUtc { get; init; }
        public int ShadowDecisionCount { get; init; }
        public int ApprovedShadowDecisionCount { get; init; }
        public int PaperOrderCount { get; init; }
        public int FilledPaperOrderCount { get; init; }
        public decimal PaperUnfilledRatio { get; init; }
        public decimal? AverageEntrySlippageBps { get; init; }
        public double? AverageFillLatencyMilliseconds { get; init; }
        public int QualityIncidentCount { get; init; }
        public int UnhealthyStreamCount { get; init; }
        public int UnknownOrderCount { get; init; }
        public bool ShadowCriteriaPassed { get; init; }
        public bool PaperCriteriaPassed { get; init; }
        public bool EligibleForManualLiveApproval { get; init; }
        public bool Suspended { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }

    public sealed record ManualLiveApprovalRequest(bool ConfirmLiveTrading, string Reason, int ExpectedVersion);
}
