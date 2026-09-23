using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class HistoricalBackfillJobRecord
    {
        public long Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public HistoricalBackfillStatus Status { get; set; }
        public DateTime DesiredStartUtc { get; set; }
        public DateTime DesiredEndUtc { get; set; }
        public DateTime NextSegmentEndUtc { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public DateTime? LastRequestedAtUtc { get; set; }
        public int CompletedSegments { get; set; }
        public int FailedAttempts { get; set; }
        public long BarsReceived { get; set; }
        public long BarsInserted { get; set; }
        public long DuplicateBars { get; set; }
        public int GapCount { get; set; }
        public string LastError { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public int Version { get; set; }
    }

    public sealed class HistoricalBackfillSegmentRecord
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public HistoricalBackfillSegmentStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public int BarsReceived { get; set; }
        public int BarsInserted { get; set; }
        public int DuplicateBars { get; set; }
        public string LastError { get; set; } = string.Empty;
        public DateTime RequestedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
    }

    public sealed class HistoricalDataGapRecord
    {
        public long Id { get; set; }
        public long JobId { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public DateTime StartUtc { get; set; }
        public DateTime EndUtc { get; set; }
        public int MissingBars { get; set; }
        public DateTime DetectedAtUtc { get; set; }
    }
}
