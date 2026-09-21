using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IExitManagementService
    {
        Task RegisterApprovedEntryAsync(
            ApprovedTradePlan plan,
            ManagedOrderResult entryOrder,
            CancellationToken cancellationToken = default);

        Task ProcessMarketCandleAsync(Candle candle, CancellationToken cancellationToken = default);

        Task RestoreAsync(CancellationToken cancellationToken = default);
    }
}
