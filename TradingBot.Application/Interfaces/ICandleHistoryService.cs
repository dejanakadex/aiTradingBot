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
        async Task<IReadOnlyList<Candle>> GetCandlesAsOfAsync(string symbol, Timeframe timeframe, int n, DateTime asOfUtc, CancellationToken cancellationToken = default)
        {
            if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC as-of required.", nameof(asOfUtc));
            return (await GetLastNCandlesAsync(symbol, timeframe, n, cancellationToken))
                .Where(x => x.TimestampUtc <= asOfUtc && x.ReceivedTimeUtc <= asOfUtc && x.IsFinal && x.QualityStatus == MarketDataQualityStatus.Healthy).ToArray();
        }
    }
}
