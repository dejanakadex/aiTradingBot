using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IHistoricalBackfillService
    {
        Task SynchronizePlanAsync(CancellationToken cancellationToken = default);
        Task<HistoricalBackfillJobSnapshot?> RunNextSegmentAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<HistoricalBackfillJobSnapshot>> GetJobsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<HistoricalDataGapSnapshot>> GetGapsAsync(string? instrumentId = null, CancellationToken cancellationToken = default);
    }
}
