using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.Interfaces
{
    public interface IOperatingModeService
    {
        TradingOperatingMode CurrentMode { get; }

        Task SetModeAsync(TradingOperatingMode mode, string reason, CancellationToken cancellationToken = default);
    }
}
