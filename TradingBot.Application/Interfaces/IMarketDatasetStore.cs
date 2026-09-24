using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDatasetStore
    {
        Task<DatasetWriteResult> WriteBatchAsync(IReadOnlyCollection<MarketDatasetRecord> records, CancellationToken cancellationToken = default);
        Task<DatasetManifest> GetManifestAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<MarketDatasetRecord>> ReadAsync(DatasetQuery query, CancellationToken cancellationToken = default);
        Task<DatasetVerificationResult> VerifyAsync(CancellationToken cancellationToken = default);
    }
}
