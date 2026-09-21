using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Application.Interfaces
{
    public interface IProtectiveStopMonitor
    {
        Task<bool> ValidateAsync(CancellationToken cancellationToken = default);
    }
}
