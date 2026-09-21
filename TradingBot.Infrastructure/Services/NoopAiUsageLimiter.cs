using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    internal sealed class NoopAiUsageLimiter : IAiUsageLimiter
    {
        public Task<AiUsageLimitDecision> CheckAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(AiUsageLimitDecision.Allow());
        }
    }
}
