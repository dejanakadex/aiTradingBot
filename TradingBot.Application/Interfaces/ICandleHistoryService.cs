using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Domain.Models;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.Interfaces
{
    public interface ICandleHistoryService
    {
        Task<IReadOnlyList<Candle>> GetLastNCandlesAsync(string symbol, Timeframe timeframe, int n, CancellationToken cancellationToken = default);
    }
}
