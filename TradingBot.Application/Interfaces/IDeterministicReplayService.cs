using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IDeterministicReplayService
    {
        Task<ReplayRunSnapshot> StartAsync(ReplayStartRequest request, CancellationToken cancellationToken = default);
        Task<ReplayRunSnapshot?> GetAsync(Guid replayRunId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ReplayRunSnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ReplaySignalSnapshot>> GetSignalsAsync(Guid replayRunId, CancellationToken cancellationToken = default);
        Task<ReplayRunSnapshot> PauseAsync(Guid replayRunId, CancellationToken cancellationToken = default);
        Task<ReplayRunSnapshot> ResumeAsync(Guid replayRunId, decimal? speedMultiplier = null, CancellationToken cancellationToken = default);
        Task<ReplayRunSnapshot> CancelAsync(Guid replayRunId, CancellationToken cancellationToken = default);
        Task<ReplayRunSnapshot> ProcessBatchAsync(Guid replayRunId, int? maximumEvents = null, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(CancellationToken cancellationToken = default);
    }
}
