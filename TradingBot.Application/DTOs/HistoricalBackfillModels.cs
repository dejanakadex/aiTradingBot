using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record HistoricalBarRequest(
        string InstrumentId,
        string Symbol,
        string Timeframe,
        DateTime StartUtc,
        DateTime EndUtc);

    public sealed record HistoricalBackfillJobSnapshot(
        long Id,
        string InstrumentId,
        string Symbol,
        string Timeframe,
        HistoricalBackfillStatus Status,
        DateTime DesiredStartUtc,
        DateTime DesiredEndUtc,
        DateTime NextSegmentEndUtc,
        DateTime? NextAttemptUtc,
        int CompletedSegments,
        int FailedAttempts,
        long BarsReceived,
        long BarsInserted,
        long DuplicateBars,
        int GapCount,
        string LastError,
        DateTime UpdatedAtUtc);

    public sealed record HistoricalDataGapSnapshot(
        long Id,
        string InstrumentId,
        string Symbol,
        string Timeframe,
        DateTime StartUtc,
        DateTime EndUtc,
        int MissingBars,
        DateTime DetectedAtUtc);
}
