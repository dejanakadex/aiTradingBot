using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradingExecutionGuard
    {
        Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(
            RiskDecision? riskDecision,
            CancellationToken cancellationToken = default);
    }
}
