using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IScalpingExecutionGate
    {
        Task<ScalpingExecutionDecision> EvaluateAsync(ApprovedTradePlan plan, CancellationToken cancellationToken = default);
    }
}
