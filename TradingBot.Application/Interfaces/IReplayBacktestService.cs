using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces;

public interface IReplayBacktestService
{
    Task<ReplayBacktestSnapshot> RunAsync(Guid replayRunId, ReplayBacktestRequest request, CancellationToken cancellationToken = default);
    Task<ReplayBacktestSnapshot?> GetAsync(Guid backtestId, CancellationToken cancellationToken = default);
}
