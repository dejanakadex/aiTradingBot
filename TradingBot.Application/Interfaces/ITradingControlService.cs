using System.Threading;
using System.Threading.Tasks;

namespace TradingBot.Application.Interfaces
{
    public interface ITradingControlService
    {
        Task PauseTradingAsync(string reason, CancellationToken cancellationToken = default);
        Task ResumeTradingAsync(string reason, CancellationToken cancellationToken = default);
        Task CloseCurrentPositionAsync(string reason, CancellationToken cancellationToken = default);
        Task KillSwitchAsync(string reason, CancellationToken cancellationToken = default);
    }
}
