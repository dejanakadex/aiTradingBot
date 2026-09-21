using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradeDashboardService
    {
        Task<IReadOnlyList<TradeDashboardRow>> GetTradesAsync(int take = 200, CancellationToken cancellationToken = default);
    }
}
