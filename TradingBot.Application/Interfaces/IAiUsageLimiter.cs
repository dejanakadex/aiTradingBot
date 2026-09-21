using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Application.Interfaces
{
    public interface IAiUsageLimiter
    {
        Task<AiUsageLimitDecision> CheckAsync(CancellationToken cancellationToken = default);
    }

    public sealed class AiUsageLimitDecision
    {
        public bool Approved { get; init; }
        public string Reason { get; init; } = string.Empty;

        public static AiUsageLimitDecision Allow() => new() { Approved = true };

        public static AiUsageLimitDecision Reject(string reason) => new()
        {
            Approved = false,
            Reason = reason
        };
    }
}
