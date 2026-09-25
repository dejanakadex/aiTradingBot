using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed record CandidatePriceObservation(
        DateTime EventTimeUtc,
        decimal High,
        decimal Low,
        decimal Close,
        string SourceEventId,
        bool IsBar = false);

    public sealed record CandidateSpreadObservation(
        DateTime EventTimeUtc,
        decimal Bid,
        decimal Ask);

    public sealed class CandidateLabelCalculation
    {
        public CandidateLabelStatus Status { get; init; }
        public TargetStopOutcome TargetStopOutcome { get; init; }
        public int ObservationCount { get; init; }
        public decimal? ExitPrice { get; init; }
        public decimal? MaximumFavorableExcursionBps { get; init; }
        public decimal? MaximumAdverseExcursionBps { get; init; }
        public decimal? GrossReturnBps { get; init; }
        public decimal? EstimatedCostBps { get; init; }
        public decimal? NetReturnBps { get; init; }
        public decimal? ObservedSpreadBps { get; init; }
        public string FirstTargetEventId { get; init; } = string.Empty;
        public string FirstStopEventId { get; init; } = string.Empty;
        public DateTime? MaximumEventTimeUtc { get; init; }
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    }
}
