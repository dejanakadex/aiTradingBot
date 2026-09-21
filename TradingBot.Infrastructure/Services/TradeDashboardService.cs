using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradeDashboardService : ITradeDashboardService
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;

        public TradeDashboardService(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<IReadOnlyList<TradeDashboardRow>> GetTradesAsync(int take = 200, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.Trades.AsNoTracking()
                .OrderByDescending(t => t.OpenedUtc)
                .Take(Math.Clamp(take, 1, 1000))
                .Select(t => new
                {
                    t.OpenedUtc,
                    t.Symbol,
                    t.EntryPrice,
                    t.ExitPrice,
                    t.Size,
                    t.Notes
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return rows.Select(t =>
            {
                var gross = t.ExitPrice.HasValue ? (t.ExitPrice.Value - t.EntryPrice) * t.Size : 0m;
                var fees = ExtractDecimal(t.Notes, "fees") ?? ExtractDecimal(t.Notes, "commission") ?? 0m;
                var net = gross - fees;
                return new TradeDashboardRow
                {
                    TimestampUtc = t.OpenedUtc,
                    Symbol = t.Symbol,
                    Pattern = ExtractString(t.Notes, "pattern") ?? "Unknown",
                    AiConfidence = ExtractDecimal(t.Notes, "aiConfidence"),
                    Entry = t.EntryPrice,
                    Exit = t.ExitPrice,
                    Quantity = t.Size,
                    GrossProfitLoss = gross,
                    Fees = fees,
                    NetProfitLoss = net,
                    TradeResult = !t.ExitPrice.HasValue ? "Open" : net > 0m ? "Win" : net < 0m ? "Loss" : "Flat"
                };
            }).ToList();
        }

        internal static string? ExtractString(string? json, string property)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return TryFindProperty(doc.RootElement, property, out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        internal static decimal? ExtractDecimal(string? json, string property)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return TryFindProperty(doc.RootElement, property, out var value) && value.TryGetDecimal(out var number)
                    ? number
                    : null;
            }
            catch (JsonException) { return null; }
        }

        private static bool TryFindProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty(propertyName, out value)) return true;

                foreach (var property in element.EnumerateObject())
                {
                    if (TryFindProperty(property.Value, propertyName, out value)) return true;
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindProperty(item, propertyName, out value)) return true;
                }
            }

            value = default;
            return false;
        }
    }
}
