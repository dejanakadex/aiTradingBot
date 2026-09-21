using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IDashboardService
    {
        Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    }
}
