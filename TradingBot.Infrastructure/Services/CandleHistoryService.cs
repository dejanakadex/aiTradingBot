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
        {
            await using var ctx = _dbFactory.CreateDbContext();
            var rows = await ctx.Candles
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
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
