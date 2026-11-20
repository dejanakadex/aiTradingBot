using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDatasetStore
    {
        Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default);
        Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default);
        Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default);
        // Unordered batches retain duplicates so the consumer can detect conflicts instead of silently choosing one.
        IAsyncEnumerable<IReadOnlyList<MarketDatasetRecord>> ReadBatchesAsync(DatasetQuery query, int batchSize,
            CancellationToken cancellationToken = default) => throw new NotSupportedException("This dataset store does not support bounded reads.");
    }
}
