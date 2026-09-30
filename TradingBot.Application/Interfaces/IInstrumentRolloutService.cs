using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IInstrumentRolloutService
    {
        Task<IReadOnlyList<InstrumentRolloutEvaluation>> EvaluateAllAsync(bool applyTransitions, CancellationToken cancellationToken = default);
        Task<InstrumentRolloutEvaluation> EvaluateAsync(string instrumentId, bool applyTransitions, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<InstrumentRolloutEvaluation>> GetRecentAsync(string? instrumentId = null, int count = 100, CancellationToken cancellationToken = default);
        Task<InstrumentRegistrySnapshot> ApproveLiveAsync(string instrumentId, ManualLiveApprovalRequest request, CancellationToken cancellationToken = default);
    }
}
