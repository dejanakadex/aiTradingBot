using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public class CandleHistoryService : ICandleHistoryService
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;

        public CandleHistoryService(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<IReadOnlyList<Candle>> GetLastNCandlesAsync(string symbol, Timeframe timeframe, int n, CancellationToken cancellationToken = default)
            => await ReadAsync(symbol, timeframe, n, null, cancellationToken);

        public Task<IReadOnlyList<Candle>> GetCandlesAsOfAsync(string symbol, Timeframe timeframe, int n, DateTime asOfUtc, CancellationToken cancellationToken = default)
        {
            if (asOfUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("UTC as-of required.", nameof(asOfUtc));
            return ReadAsync(symbol, timeframe, n, asOfUtc, cancellationToken);
        }

        private async Task<IReadOnlyList<Candle>> ReadAsync(string symbol, Timeframe timeframe, int n, DateTime? asOfUtc, CancellationToken cancellationToken)
        {
            await using var ctx = _dbFactory.CreateDbContext();
            var query = ctx.Candles.Where(c => c.Symbol == symbol && c.Timeframe == timeframe);
            // Filter before Take: later rows must not evict the decision's historical window.
            if (asOfUtc.HasValue) query = query.Where(c => c.TimestampUtc <= asOfUtc.Value && c.ReceivedTimeUtc <= asOfUtc.Value
                && c.IsFinal && c.QualityStatus == MarketDataQualityStatus.Healthy);
            var rows = await query
                .OrderByDescending(c => c.TimestampUtc)
                .Take(n)
                .AsNoTracking()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            // map to domain Candles, order ascending
            var mapped = rows.Select(r => new Candle(
                    r.Symbol,
                    r.Timeframe,
                    r.TimestampUtc,
                    r.Open,
                    r.High,
                    r.Low,
                    r.Close,
                    r.Volume,
                    r.InstrumentId,
                    r.ReceivedTimeUtc,
                    r.Source,
                    r.IsFinal,
                    r.QualityStatus))
                .OrderBy(c => c.TimestampUtc)
                .ToList();

            return mapped;
        }
    }
}
