using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketSnapshotService
    {
        Task<MarketSnapshot> BuildSnapshotAsOfAsync(string symbol, DateTime asOfUtc, CancellationToken cancellationToken = default);
        Task<MarketSnapshot> BuildSnapshotAsync(
            string symbol,
            decimal? currentPrice = null,
            decimal? spread = null,
            CancellationToken cancellationToken = default);
    }
}
