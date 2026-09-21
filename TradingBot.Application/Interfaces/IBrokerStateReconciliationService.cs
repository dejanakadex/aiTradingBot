using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IBrokerStateReconciliationService
    {
        Task<BrokerReconciliationStatus> ReconcileAsync(CancellationToken cancellationToken = default);
    }
}
