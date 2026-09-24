using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record ReplayStartRequest(
        string InstrumentId,
        DateTime FromUtc,
        DateTime ToUtc,
        decimal SpeedMultiplier = 0m,
        string StrategyId = "deterministic-patterns");

    public sealed record ReplaySpeedRequest(decimal SpeedMultiplier);

    public sealed record ReplayRunSnapshot(
        Guid Id,
        string InstrumentId,
        string Symbol,
        string StrategyId,
        DateTime FromUtc,
        DateTime ToUtc,
        decimal SpeedMultiplier,
        ReplayRunStatus Status,
        string InputSha256,
        int InputEventCount,
        int ProcessedEventCount,
        int SignalCount,
        string CheckpointEventId,
        DateTime? CheckpointEventTimeUtc,
        string OutputSha256,
        string MarketDataVersion,
        string FeatureVersion,
        string PatternVersion,
        string StrategyVersion,
        string LastError,
        DateTime CreatedAtUtc,
        DateTime? StartedAtUtc,
        DateTime UpdatedAtUtc,
        DateTime? CompletedAtUtc);

    public sealed record ReplaySignalSnapshot(
        long Id,
        Guid ReplayRunId,
        Guid SignalId,
        string SourceEventId,
        PatternType PatternType,
        string Symbol,
        Timeframe Timeframe,
        DateTime DetectedAtUtc,
        decimal Confidence,
        string RelevantPriceLevelsJson,
        string MetadataJson,
        string FeaturesJson);
}
