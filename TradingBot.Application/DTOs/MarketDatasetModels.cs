using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed class MarketDatasetRecord
    {
        public string SchemaVersion { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public DateTime EventTimeUtc { get; set; }
        public DateTime ReceivedTimeUtc { get; set; }
        public string Source { get; set; } = string.Empty;
        public long? Sequence { get; set; }
        public bool IsFinal { get; set; }
        public string Timeframe { get; set; } = string.Empty;
        public decimal? Price { get; set; }
        public decimal? Size { get; set; }
        public decimal? Open { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Close { get; set; }
        public decimal? Volume { get; set; }
        public string QualityStatus { get; set; } = string.Empty;
        public string QualityReason { get; set; } = string.Empty;
        public bool CanPersist { get; set; }
        public bool CanTriggerTrading { get; set; }

        public static MarketDatasetRecord From(
            CanonicalMarketDataEvent marketEvent,
            MarketDataQualityAssessment assessment,
            string schemaVersion) => new()
            {
                SchemaVersion = schemaVersion,
                EventId = marketEvent.EventId,
                InstrumentId = marketEvent.InstrumentId,
                Symbol = marketEvent.Symbol,
                DataType = marketEvent.Kind.ToString().ToLowerInvariant(),
                EventTimeUtc = marketEvent.EventTimeUtc,
                ReceivedTimeUtc = marketEvent.ReceivedTimeUtc,
                Source = marketEvent.Source,
                Sequence = marketEvent.Sequence,
                IsFinal = marketEvent.IsFinal,
                Timeframe = marketEvent.Timeframe,
                Price = marketEvent.Price,
                Size = marketEvent.Size,
                Open = marketEvent.Open,
                High = marketEvent.High,
                Low = marketEvent.Low,
                Close = marketEvent.Close,
                Volume = marketEvent.Volume,
                QualityStatus = assessment.Status.ToString(),
                QualityReason = assessment.Reason,
                CanPersist = assessment.CanPersist,
                CanTriggerTrading = assessment.CanTriggerTrading
            };
    }

    public sealed class DatasetFileManifestEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string InstrumentId { get; set; } = string.Empty;
        public DateTime DateUtc { get; set; }
        public string DataType { get; set; } = string.Empty;
        public int RowCount { get; set; }
        public DateTime MinimumEventTimeUtc { get; set; }
        public DateTime MaximumEventTimeUtc { get; set; }
        public string ContentSha256 { get; set; } = string.Empty;
        public string SchemaVersion { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }

    public sealed class DatasetManifest
    {
        public string DatasetVersion { get; set; } = "1";
        public string SchemaVersion { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; }
        public List<DatasetFileManifestEntry> Files { get; set; } = new();
    }

    public sealed record DatasetQuery(
        string? InstrumentId = null,
        DateTime? FromUtc = null,
        DateTime? ToUtc = null,
        IReadOnlyCollection<string>? DataTypes = null);

    public sealed record DatasetWriteResult(int FilesCreated, int RowsWritten, int FilesSkipped);

    public sealed record DatasetVerificationResult(
        bool IsValid,
        int FileCount,
        long RowCount,
        string ManifestSha256,
        IReadOnlyList<string> Errors);
}
