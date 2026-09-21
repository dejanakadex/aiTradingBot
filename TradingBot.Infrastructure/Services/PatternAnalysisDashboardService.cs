using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TradingBot.Domain.Enums;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PatternAnalysisDashboardService : IPatternAnalysisDashboardService
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;

        public PatternAnalysisDashboardService(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory)
        {
            _dbFactory = dbFactory;
        }

        public async Task<PatternAnalysisDashboard> GetAnalysisAsync(PatternAnalysisFilter filter, int take = 200, CancellationToken cancellationToken = default)
        {
            filter ??= new PatternAnalysisFilter();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var patternQuery = db.PatternDetections.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(filter.Pattern)) patternQuery = patternQuery.Where(p => p.PatternType.ToString() == filter.Pattern);
            if (!string.IsNullOrWhiteSpace(filter.Symbol)) patternQuery = patternQuery.Where(p => p.Symbol == filter.Symbol);
            if (filter.FromUtc.HasValue) patternQuery = patternQuery.Where(p => p.DetectedAtUtc >= filter.FromUtc.Value);
            if (filter.ToUtc.HasValue) patternQuery = patternQuery.Where(p => p.DetectedAtUtc <= filter.ToUtc.Value);

            var detectionCounts = await patternQuery
                .GroupBy(p => p.PatternType)
                .Select(g => new { Pattern = g.Key.ToString(), Detections = g.Count() })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var tradeQuery = db.Trades.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(filter.Symbol)) tradeQuery = tradeQuery.Where(t => t.Symbol == filter.Symbol);
            if (filter.FromUtc.HasValue) tradeQuery = tradeQuery.Where(t => t.OpenedUtc >= filter.FromUtc.Value);
            if (filter.ToUtc.HasValue) tradeQuery = tradeQuery.Where(t => t.OpenedUtc <= filter.ToUtc.Value);

            var tradeRows = await tradeQuery
                .Where(t => t.ClosedUtc.HasValue && t.ExitPrice.HasValue)
                .Select(t => new
                {
                    t.Symbol,
                    t.OpenedUtc,
                    t.EntryPrice,
                    t.ExitPrice,
                    t.Size,
                    t.Notes
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var filteredTrades = tradeRows
                .Where(t => MatchesJsonFilters(t.Notes, filter))
                .Select(t =>
                {
                    var gross = (t.ExitPrice!.Value - t.EntryPrice) * t.Size;
                    var fees = TradeDashboardService.ExtractDecimal(t.Notes, "fees") ?? TradeDashboardService.ExtractDecimal(t.Notes, "commission") ?? 0m;
                    var pattern = TradeDashboardService.ExtractString(t.Notes, "pattern") ?? "Unknown";
                    return new { Pattern = pattern, Gross = gross, Net = gross - fees };
                })
                .ToList();

            var statistics = detectionCounts
                .GroupJoin(
                    filteredTrades,
                    d => d.Pattern,
                    t => t.Pattern,
                    (d, trades) =>
                    {
                        var list = trades.ToList();
                        var wins = list.Count(t => t.Net > 0m);
                        var losses = list.Count(t => t.Net < 0m);
                        var grossProfit = list.Where(t => t.Net > 0m).Sum(t => t.Net);
                        var grossLoss = Math.Abs(list.Where(t => t.Net < 0m).Sum(t => t.Net));
                        return new PatternStatisticsRow
                        {
                            Pattern = d.Pattern,
                            Detections = d.Detections,
                            Trades = list.Count,
                            Wins = wins,
                            Losses = losses,
                            WinRate = list.Count == 0 ? 0m : (decimal)wins / list.Count,
                            AverageGrossReturn = list.Count == 0 ? 0m : list.Average(t => t.Gross),
                            AverageNetReturn = list.Count == 0 ? 0m : list.Average(t => t.Net),
                            ProfitFactor = grossLoss == 0m ? grossProfit > 0m ? null : 0m : grossProfit / grossLoss
                        };
                    })
                .OrderByDescending(s => s.Detections)
                .ToList();

            var analysisRows = await BuildAnalysisRowsAsync(db, patternQuery, filter, take, cancellationToken).ConfigureAwait(false);

            return new PatternAnalysisDashboard
            {
                Statistics = statistics,
                Rows = analysisRows
            };
        }

        private static async Task<IReadOnlyList<PatternAnalysisRow>> BuildAnalysisRowsAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            IQueryable<TradingBot.Persistence.PatternDetection> patternQuery,
            PatternAnalysisFilter filter,
            int take,
            CancellationToken cancellationToken)
        {
            var patterns = await patternQuery
                .OrderByDescending(p => p.DetectedAtUtc)
                .Take(Math.Clamp(take, 1, 1000))
                .Select(p => new { p.Symbol, p.PatternType, p.DetectedAtUtc, p.Details })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var from = patterns.Count == 0 ? DateTime.UtcNow : patterns.Min(p => p.DetectedAtUtc).AddHours(-2);
            var to = patterns.Count == 0 ? DateTime.UtcNow : patterns.Max(p => p.DetectedAtUtc).AddHours(2);

            var analyzerRecords = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.TimestampUtc >= from && a.TimestampUtc <= to)
                .Where(a => a.AgentType == nameof(AiAgentType.Analyzer))
                .OrderByDescending(a => a.TimestampUtc)
                .Select(a => new AiRecord(a.TimestampUtc, a.Context, a.ResultJson))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var criticRecords = await db.AiAnalysisRecords.AsNoTracking()
                .Where(a => a.TimestampUtc >= from && a.TimestampUtc <= to)
                .Where(a => a.AgentType == nameof(AiAgentType.Critic))
                .OrderByDescending(a => a.TimestampUtc)
                .Select(a => new AiRecord(a.TimestampUtc, a.Context, a.ResultJson))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var strategyRecords = await db.StrategyDecisionRecords.AsNoTracking()
                .Where(s => s.TimestampUtc >= from && s.TimestampUtc <= to)
                .OrderByDescending(s => s.TimestampUtc)
                .Select(s => new { s.TimestampUtc, s.DecisionJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var riskRecords = await db.RiskDecisionRecords.AsNoTracking()
                .Where(r => r.TimestampUtc >= from && r.TimestampUtc <= to)
                .OrderByDescending(r => r.TimestampUtc)
                .Select(r => new { r.TimestampUtc, r.DecisionJson })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var trades = await db.Trades.AsNoTracking()
                .Where(t => t.OpenedUtc >= from && t.OpenedUtc <= to)
                .Select(t => new { t.Symbol, t.OpenedUtc, t.EntryPrice, t.ExitPrice, t.Size, t.Notes })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return patterns.Select(p =>
            {
                var patternName = p.PatternType.ToString();
                var ai = FindAssociatedAiRecord(analyzerRecords, p.Symbol, patternName, p.DetectedAtUtc);
                var critic = FindAssociatedAiRecord(criticRecords, p.Symbol, patternName, p.DetectedAtUtc);
                var strategy = strategyRecords.OrderBy(s => Math.Abs((s.TimestampUtc - p.DetectedAtUtc).TotalSeconds)).FirstOrDefault();
                var risk = riskRecords.OrderBy(r => Math.Abs((r.TimestampUtc - p.DetectedAtUtc).TotalSeconds)).FirstOrDefault();
                var trade = trades.Where(t => t.Symbol == p.Symbol).OrderBy(t => Math.Abs((t.OpenedUtc - p.DetectedAtUtc).TotalSeconds)).FirstOrDefault();
                var gross = trade?.ExitPrice.HasValue == true ? (trade.ExitPrice.Value - trade.EntryPrice) * trade.Size : 0m;
                return new PatternAnalysisRow
                {
                    TimestampUtc = p.DetectedAtUtc,
                    Symbol = p.Symbol,
                    PatternCandidate = p.PatternType.ToString(),
                    AiDecision = TradeDashboardService.ExtractString(ai?.ResultJson, "action") ?? "Unknown",
                    AiConfidence = TradeDashboardService.ExtractDecimal(ai?.ResultJson, "confidence"),
                    CriticDecision = TradeDashboardService.ExtractString(critic?.ResultJson, "approved") ?? "Unknown",
                    StrategyDecision = TradeDashboardService.ExtractString(strategy?.DecisionJson, "approved") ?? "Unknown",
                    RiskDecision = TradeDashboardService.ExtractString(risk?.DecisionJson, "decision") ?? "Unknown",
                    ActualTradeOutcome = trade == null ? "No trade" : !trade.ExitPrice.HasValue ? "Open" : gross > 0m ? "Win" : gross < 0m ? "Loss" : "Flat",
                    MarketRegime = TradeDashboardService.ExtractString(ai?.ResultJson, "marketRegime") ?? string.Empty,
                    AboveVwap = ExtractBool(p.Details, "aboveVwap"),
                    FifteenMinuteTrend = TradeDashboardService.ExtractString(p.Details, "fifteenMinuteTrend") ?? string.Empty
                };
            })
            .Where(r => MatchesRowFilters(r, filter))
            .ToList();
        }

        private static AiRecord? FindAssociatedAiRecord(
            IReadOnlyList<AiRecord> records,
            string symbol,
            string pattern,
            DateTime detectedAtUtc)
        {
            var detectedAt = ToUtc(detectedAtUtc);

            return records
                .Where(record => IsNearDetection(record.TimestampUtc, detectedAt))
                .Where(record => MatchesSymbolAndPattern(record, symbol, pattern))
                .OrderBy(record => Math.Abs((ToUtc(record.TimestampUtc) - detectedAt).TotalSeconds))
                .FirstOrDefault();
        }

        private static bool IsNearDetection(DateTime recordTimestampUtc, DateTime detectedAtUtc)
        {
            var delta = ToUtc(recordTimestampUtc) - detectedAtUtc;
            return delta >= TimeSpan.FromMinutes(-1) && delta <= TimeSpan.FromMinutes(15);
        }

        private static bool MatchesSymbolAndPattern(AiRecord record, string symbol, string pattern)
        {
            var context = record.Context ?? string.Empty;
            if (string.IsNullOrWhiteSpace(context))
            {
                return false;
            }

            var hasSymbol = JsonContainsStringValue(context, symbol);
            if (!hasSymbol)
            {
                return false;
            }

            var hasPattern = JsonContainsStringValue(context, pattern)
                || JsonContainsStringValue(record.ResultJson, pattern);

            return hasPattern;
        }

        private static bool JsonContainsStringValue(string? json, string expected)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                return ContainsStringValue(doc.RootElement, expected);
            }
            catch (JsonException)
            {
                return json.Contains(expected, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool ContainsStringValue(JsonElement element, string expected)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                return string.Equals(element.GetString(), expected, StringComparison.OrdinalIgnoreCase);
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, expected, StringComparison.OrdinalIgnoreCase)
                        || ContainsStringValue(property.Value, expected))
                    {
                        return true;
                    }
                }
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsStringValue(item, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool MatchesJsonFilters(string notes, PatternAnalysisFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.Pattern) && TradeDashboardService.ExtractString(notes, "pattern") != filter.Pattern) return false;
            if (!string.IsNullOrWhiteSpace(filter.MarketRegime) && TradeDashboardService.ExtractString(notes, "marketRegime") != filter.MarketRegime) return false;
            if (!string.IsNullOrWhiteSpace(filter.FifteenMinuteTrend) && TradeDashboardService.ExtractString(notes, "fifteenMinuteTrend") != filter.FifteenMinuteTrend) return false;
            if (!string.IsNullOrWhiteSpace(filter.AiDecision) && TradeDashboardService.ExtractString(notes, "aiDecision") != filter.AiDecision) return false;
            if (filter.AboveVwap.HasValue && ExtractBool(notes, "aboveVwap") != filter.AboveVwap) return false;
            return true;
        }

        private static bool MatchesRowFilters(PatternAnalysisRow row, PatternAnalysisFilter filter)
        {
            if (!string.IsNullOrWhiteSpace(filter.MarketRegime) && row.MarketRegime != filter.MarketRegime) return false;
            if (!string.IsNullOrWhiteSpace(filter.FifteenMinuteTrend) && row.FifteenMinuteTrend != filter.FifteenMinuteTrend) return false;
            if (!string.IsNullOrWhiteSpace(filter.AiDecision) && row.AiDecision != filter.AiDecision) return false;
            if (filter.AboveVwap.HasValue && row.AboveVwap != filter.AboveVwap) return false;
            return true;
        }

        private static bool? ExtractBool(string? json, string property)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : null;
            }
            catch (JsonException) { return null; }
        }

        private static DateTime ToUtc(DateTime timestamp)
        {
            return timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            };
        }

        private sealed record AiRecord(DateTime TimestampUtc, string Context, string ResultJson);
    }
}
