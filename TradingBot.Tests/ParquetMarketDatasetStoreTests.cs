using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class ParquetMarketDatasetStoreTests
    {
        private static readonly DateTime NowUtc = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public async Task WritePartitionsManifestAndReadAreReproducibleAndIdempotent()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var store = CreateStore(root);
                var records = new[]
                {
                    Record("trade-2", "US-STK-SPY-SMART", "trade", NowUtc.AddSeconds(2), 100.02m),
                    Record("trade-1", "US-STK-SPY-SMART", "trade", NowUtc.AddSeconds(1), 100.01m),
                    Record("ask-next-day", "US-STK-SPY-SMART", "ask", NowUtc.AddDays(1), 101.02m),
                    Record("bid-qqq", "US-STK-QQQ-SMART", "bid", NowUtc, 500.01m)
                };

                var first = await store.WriteBatchAsync(records);
                var second = await store.WriteBatchAsync(records.Reverse().ToArray());
                var manifest = await store.GetManifestAsync();
                var verification = await store.VerifyAsync();
                var read = await store.ReadAsync(new DatasetQuery(
                    "US-STK-SPY-SMART",
                    NowUtc.Date,
                    NowUtc.Date.AddDays(1),
                    new[] { "trade" }));

                Assert.Equal(3, first.FilesCreated);
                Assert.Equal(4, first.RowsWritten);
                Assert.Equal(0, second.FilesCreated);
                Assert.Equal(3, second.FilesSkipped);
                Assert.Equal(3, manifest.Files.Count);
                Assert.All(manifest.Files, file => Assert.Equal("market-data-v2.parquet-v1", file.SchemaVersion));
                Assert.Contains(manifest.Files, file => file.RelativePath.StartsWith("instrument=US-STK-SPY-SMART/date=2026-09-24/type=trade/", StringComparison.Ordinal));
                Assert.True(verification.IsValid);
                Assert.Equal(4, verification.RowCount);
                Assert.Equal(64, verification.ManifestSha256.Length);
                Assert.Equal(new[] { "trade-1", "trade-2" }, read.Select(item => item.EventId));
                Assert.All(read, item => Assert.Equal(DateTimeKind.Utc, item.EventTimeUtc.Kind));
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        [Fact]
        public async Task VerificationAndReadRejectTamperedParquetFile()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var store = CreateStore(root);
                await store.WriteBatchAsync(new[] { Record("trade-1", "US-STK-SPY-SMART", "trade", NowUtc, 100m) });
                var entry = Assert.Single((await store.GetManifestAsync()).Files);
                var path = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                await File.AppendAllTextAsync(path, "tampered");

                var verification = await store.VerifyAsync();

                Assert.False(verification.IsValid);
                Assert.Contains(verification.Errors, error => error.Contains("SHA-256 mismatch", StringComparison.Ordinal));
                await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(new DatasetQuery()));
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        [Fact]
        public async Task CanonicalFeaturesAreIdenticalBeforeAndAfterBackfillParquetRoundTrip()
        {
            var root = CreateTemporaryRoot();
            try
            {
                var store = CreateStore(root);
                var records = Enumerable.Range(0, 8)
                    .Select(index => BarRecord(index))
                    .Reverse()
                    .ToArray();
                await store.WriteBatchAsync(records);
                var restored = await store.ReadAsync(new DatasetQuery(
                    "US-STK-SPY-SMART",
                    NowUtc,
                    NowUtc.AddMinutes(8),
                    new[] { "bar" }));
                var engine = new FeatureEngine(new CanonicalFeatureSettings
                {
                    EmaShortPeriod = 2,
                    EmaLongPeriod = 4,
                    RsiPeriod = 2,
                    AtrPeriod = 2,
                    VolumeAveragePeriod = 3,
                    MomentumLookbackCandles = 2,
                    MeanReversionLookbackCandles = 3,
                    MaximumQuoteAgeSeconds = 30
                });
                var asOfUtc = NowUtc.AddMinutes(7);

                var beforeBackfill = engine.ComputeFeatures(new CanonicalFeatureInput
                {
                    Candles = records.Select(ToCandle).ToArray(),
                    AsOfUtc = asOfUtc,
                    Spread = 0.02m,
                    SpreadTimeUtc = asOfUtc
                });
                var afterBackfill = engine.ComputeFeatures(new CanonicalFeatureInput
                {
                    Candles = restored.Select(ToCandle).ToArray(),
                    AsOfUtc = asOfUtc,
                    Spread = 0.02m,
                    SpreadTimeUtc = asOfUtc
                });

                Assert.Equal(
                    System.Text.Json.JsonSerializer.Serialize(beforeBackfill),
                    System.Text.Json.JsonSerializer.Serialize(afterBackfill));
            }
            finally
            {
                DeleteTemporaryRoot(root);
            }
        }

        private static ParquetMarketDatasetStore CreateStore(string root) => new(
            Options.Create(new DatasetStorageSettings
            {
                RootPath = root,
                QueueCapacity = 100,
                BatchSize = 10,
                FlushIntervalSeconds = 1
            }),
            new FixedClock(NowUtc),
            NullLogger<ParquetMarketDatasetStore>.Instance);

        private static MarketDatasetRecord Record(string eventId, string instrumentId, string dataType, DateTime eventTimeUtc, decimal price) => new()
        {
            SchemaVersion = "ignored-on-write",
            EventId = eventId,
            InstrumentId = instrumentId,
            Symbol = instrumentId.Contains("QQQ", StringComparison.Ordinal) ? "QQQ" : "SPY",
            DataType = dataType,
            EventTimeUtc = eventTimeUtc,
            ReceivedTimeUtc = eventTimeUtc.AddMilliseconds(10),
            Source = "Test",
            IsFinal = true,
            Price = price,
            Size = 1m,
            QualityStatus = "Healthy",
            QualityReason = "Test record.",
            CanPersist = true,
            CanTriggerTrading = true
        };

        private static MarketDatasetRecord BarRecord(int minute) => new()
        {
            SchemaVersion = "ignored-on-write",
            EventId = $"bar-{minute}",
            InstrumentId = "US-STK-SPY-SMART",
            Symbol = "SPY",
            DataType = "bar",
            EventTimeUtc = NowUtc.AddMinutes(minute),
            ReceivedTimeUtc = NowUtc.AddMinutes(minute).AddMilliseconds(10),
            Source = "Test.Backfill",
            Sequence = minute,
            IsFinal = true,
            Timeframe = "1m",
            Open = 100m + minute,
            High = 101m + minute,
            Low = 99m + minute,
            Close = 100.5m + minute,
            Volume = 1_000m + minute * 100m,
            QualityStatus = MarketDataQualityStatus.Healthy.ToString(),
            QualityReason = "Validated backfill bar.",
            CanPersist = true,
            CanTriggerTrading = true
        };

        private static Candle ToCandle(MarketDatasetRecord record) => new(
            record.Symbol,
            Timeframe.OneMinute,
            record.EventTimeUtc,
            record.Open!.Value,
            record.High!.Value,
            record.Low!.Value,
            record.Close!.Value,
            record.Volume!.Value,
            record.InstrumentId,
            record.ReceivedTimeUtc,
            record.Source,
            record.IsFinal,
            MarketDataQualityStatus.Healthy);

        private static string CreateTemporaryRoot() => Path.Combine(Path.GetTempPath(), $"tradingbot-dataset-{Guid.NewGuid():N}");

        private static void DeleteTemporaryRoot(string root)
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; }
        }
    }
}
