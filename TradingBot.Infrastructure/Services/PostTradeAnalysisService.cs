using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PostTradeAnalysisService : IPostTradeAnalysisService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly IDbContextFactory<TradingBotDbContext> _dbContextFactory;

        public PostTradeAnalysisService(IDbContextFactory<TradingBotDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        public async Task<PostTradeAnalysisRecordSummary> StoreCompletedTradeAnalysisAsync(PostTradeAnalysisInput input, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(input.Symbol))
                throw new ArgumentException("Symbol is required.", nameof(input));
            if (input.Quantity == 0m)
                throw new ArgumentException("Quantity cannot be zero.", nameof(input));
            if (input.ClosedUtc < input.OpenedUtc)
                throw new ArgumentException("ClosedUtc must be greater than or equal to OpenedUtc.", nameof(input));

            var grossProfitLoss = (input.ExitPrice - input.EntryPrice) * input.Quantity;
            var netProfitLoss = grossProfitLoss - input.CommissionsFees;
            var (maximumFavorableExcursion, maximumAdverseExcursion) = CalculateExcursions(input);
            var holdingDurationMinutes = Convert.ToDecimal((input.ClosedUtc - input.OpenedUtc).TotalMinutes);
            var tradeResult = ResolveTradeResult(input.TradeResult, netProfitLoss);

            var record = new PostTradeAnalysisRecord
            {
                SourceTradeId = input.SourceTradeId,
                Symbol = input.Symbol,
                Pattern = input.PatternCandidate?.PatternType.ToString() ?? "Unknown",
                OpenedUtc = NormalizeUtc(input.OpenedUtc),
                ClosedUtc = NormalizeUtc(input.ClosedUtc),
                Quantity = input.Quantity,
                EntryPrice = input.EntryPrice,
                ExitPrice = input.ExitPrice,
                GrossProfitLoss = grossProfitLoss,
                CommissionsFees = input.CommissionsFees,
                NetProfitLoss = netProfitLoss,
                MaximumFavorableExcursion = maximumFavorableExcursion,
                MaximumAdverseExcursion = maximumAdverseExcursion,
                HoldingDurationMinutes = holdingDurationMinutes,
                MarketRegime = input.AiMarketAnalysis.MarketRegime,
                AboveVwap = ResolveAboveVwap(input),
                FifteenMinuteTrend = input.MarketSnapshot.FifteenMinutes.Trend.Label,
                AiDecision = input.AiMarketAnalysis.Action,
                AiConfidence = input.AiMarketAnalysis.Confidence,
                CriticDecision = input.AiCriticAnalysis.Approved ? "Approved" : "Rejected",
                StrategyDecision = input.StrategyDecision.Approved ? "Approved" : "Rejected",
                RiskDecision = input.RiskDecision?.Decision.ToString() ?? "Unknown",
                TradeResult = tradeResult,
                MarketSnapshotJson = Serialize(input.MarketSnapshot),
                PatternCandidateJson = Serialize(input.PatternCandidate),
                AiMarketAnalysisJson = Serialize(input.AiMarketAnalysis),
                AiCriticAnalysisJson = Serialize(input.AiCriticAnalysis),
                StrategyDecisionJson = Serialize(input.StrategyDecision),
                RiskDecisionJson = Serialize(input.RiskDecision),
                OrderDataJson = Serialize(input.Orders),
                ExecutionDataJson = Serialize(input.Executions),
                TradeResultJson = Serialize(new
                {
                    input.Symbol,
                    input.OpenedUtc,
                    input.ClosedUtc,
                    input.Quantity,
                    input.EntryPrice,
                    input.ExitPrice,
                    GrossProfitLoss = grossProfitLoss,
                    input.CommissionsFees,
                    NetProfitLoss = netProfitLoss,
                    MaximumFavorableExcursion = maximumFavorableExcursion,
                    MaximumAdverseExcursion = maximumAdverseExcursion,
                    HoldingDurationMinutes = holdingDurationMinutes,
                    Result = tradeResult
                }),
                CreatedUtc = DateTime.UtcNow
            };

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            db.PostTradeAnalysisRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);

            return ToSummary(record);
        }

        public async Task<PatternStatisticsRow> GetPatternPerformanceAsync(string pattern, PatternAnalysisFilter? filter = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                throw new ArgumentException("Pattern is required.", nameof(pattern));

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var query = ApplyFilter(db.PostTradeAnalysisRecords.AsNoTracking(), filter) 
                .Where(x => x.Pattern == pattern);

            var aggregate = await query
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Trades = g.Count(),
                    Wins = g.Count(x => x.NetProfitLoss > 0m),
                    Losses = g.Count(x => x.NetProfitLoss < 0m),
                    AverageGrossReturn = g.Average(x => (decimal?)x.GrossProfitLoss) ?? 0m,
                    AverageNetReturn = g.Average(x => (decimal?)x.NetProfitLoss) ?? 0m,
                    GrossProfit = g.Where(x => x.NetProfitLoss > 0m).Sum(x => (decimal?)x.NetProfitLoss) ?? 0m,
                    GrossLoss = g.Where(x => x.NetProfitLoss < 0m).Sum(x => (decimal?)x.NetProfitLoss) ?? 0m
                })
                .SingleOrDefaultAsync(cancellationToken);

            if (aggregate is null)
            {
                return new PatternStatisticsRow { Pattern = pattern };
            }

            return new PatternStatisticsRow
            {
                Pattern = pattern,
                Detections = aggregate.Trades,
                Trades = aggregate.Trades,
                Wins = aggregate.Wins,
                Losses = aggregate.Losses,
                WinRate = aggregate.Trades == 0 ? 0m : decimal.Divide(aggregate.Wins, aggregate.Trades),
                AverageGrossReturn = aggregate.AverageGrossReturn,
                AverageNetReturn = aggregate.AverageNetReturn,
                ProfitFactor = aggregate.GrossLoss == 0m
                    ? aggregate.GrossProfit > 0m ? null : 0m
                    : aggregate.GrossProfit / Math.Abs(aggregate.GrossLoss)
            };
        }

        public async Task<IReadOnlyList<LosingConditionSummary>> GetCommonLosingConditionsAsync(PatternAnalysisFilter? filter = null, int take = 10, CancellationToken cancellationToken = default)
        {
            if (take <= 0)
                return Array.Empty<LosingConditionSummary>();

            await using var db = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var losingTrades = ApplyFilter(db.PostTradeAnalysisRecords.AsNoTracking(), filter)
                .Where(x => x.NetProfitLoss < 0m);

            var summaries = new List<LosingConditionSummary>();
            summaries.AddRange(await GroupLosingConditionAsync(losingTrades, "Pattern", x => x.Pattern, cancellationToken));
            summaries.AddRange(await GroupLosingConditionAsync(losingTrades, "MarketRegime", x => x.MarketRegime, cancellationToken));
            summaries.AddRange(await GroupLosingConditionAsync(losingTrades, "FifteenMinuteTrend", x => x.FifteenMinuteTrend, cancellationToken));
            summaries.AddRange(await GroupLosingConditionAsync(losingTrades, "AiDecision", x => x.AiDecision, cancellationToken));
            summaries.AddRange(await losingTrades
                .Where(x => x.AboveVwap.HasValue)
                .GroupBy(x => x.AboveVwap!.Value)
                .Select(g => new LosingConditionSummary
                {
                    ConditionType = "AboveVwap",
                    ConditionValue = g.Key ? "True" : "False",
                    LosingTrades = g.Count()
                })
                .ToListAsync(cancellationToken));

            return summaries
                .OrderByDescending(x => x.LosingTrades)
                .ThenBy(x => x.ConditionType)
                .ThenBy(x => x.ConditionValue)
                .Take(take)
                .ToArray();
        }

        public async Task<IReadOnlyList<StrategyImprovementRecommendation>> GetStrategyImprovementRecommendationsAsync(PatternAnalysisFilter? filter = null, CancellationToken cancellationToken = default)
        {
            var commonLosingConditions = await GetCommonLosingConditionsAsync(filter, 5, cancellationToken);
            return commonLosingConditions
                .Select(x => new StrategyImprovementRecommendation
                {
                    Recommendation = $"Review trades where {x.ConditionType} is {x.ConditionValue}.",
                    Rationale = $"{x.LosingTrades} losing completed trade(s) matched this condition. This is analysis only and does not change strategy configuration.",
                    RequiresManualApproval = true
                })
                .ToArray();
        }

        private static IQueryable<PostTradeAnalysisRecord> ApplyFilter(IQueryable<PostTradeAnalysisRecord> query, PatternAnalysisFilter? filter)
        {
            if (filter is null)
                return query;

            if (!string.IsNullOrWhiteSpace(filter.Pattern))
                query = query.Where(x => x.Pattern == filter.Pattern);
            if (!string.IsNullOrWhiteSpace(filter.Symbol))
                query = query.Where(x => x.Symbol == filter.Symbol);
            if (filter.FromUtc.HasValue)
                query = query.Where(x => x.ClosedUtc >= filter.FromUtc.Value);
            if (filter.ToUtc.HasValue)
                query = query.Where(x => x.ClosedUtc <= filter.ToUtc.Value);
            if (!string.IsNullOrWhiteSpace(filter.MarketRegime))
                query = query.Where(x => x.MarketRegime == filter.MarketRegime);
            if (filter.AboveVwap.HasValue)
                query = query.Where(x => x.AboveVwap == filter.AboveVwap);
            if (!string.IsNullOrWhiteSpace(filter.FifteenMinuteTrend))
                query = query.Where(x => x.FifteenMinuteTrend == filter.FifteenMinuteTrend);
            if (!string.IsNullOrWhiteSpace(filter.AiDecision))
                query = query.Where(x => x.AiDecision == filter.AiDecision);

            return query;
        }

        private static async Task<IReadOnlyList<LosingConditionSummary>> GroupLosingConditionAsync(
            IQueryable<PostTradeAnalysisRecord> query,
            string conditionType,
            System.Linq.Expressions.Expression<Func<PostTradeAnalysisRecord, string>> selector,
            CancellationToken cancellationToken)
        {
            return await query
                .GroupBy(selector)
                .Where(g => g.Key != string.Empty)
                .Select(g => new LosingConditionSummary
                {
                    ConditionType = conditionType,
                    ConditionValue = g.Key,
                    LosingTrades = g.Count()
                })
                .ToListAsync(cancellationToken);
        }

        private static (decimal? MaximumFavorableExcursion, decimal? MaximumAdverseExcursion) CalculateExcursions(PostTradeAnalysisInput input)
        {
            if (input.CandlesDuringTrade.Count == 0)
                return (null, null);

            var absoluteQuantity = Math.Abs(input.Quantity);
            if (input.Quantity > 0m)
            {
                var high = input.CandlesDuringTrade.Max(x => x.High);
                var low = input.CandlesDuringTrade.Min(x => x.Low);
                return (Math.Max(0m, high - input.EntryPrice) * absoluteQuantity, Math.Max(0m, input.EntryPrice - low) * absoluteQuantity);
            }

            var shortLow = input.CandlesDuringTrade.Min(x => x.Low);
            var shortHigh = input.CandlesDuringTrade.Max(x => x.High);
            return (Math.Max(0m, input.EntryPrice - shortLow) * absoluteQuantity, Math.Max(0m, shortHigh - input.EntryPrice) * absoluteQuantity);
        }

        private static bool? ResolveAboveVwap(PostTradeAnalysisInput input)
        {
            if (input.MarketSnapshot.OneMinute.Features?.DistanceFromVwapPercent is decimal distance)
                return distance >= 0m;

            if (input.MarketSnapshot.CurrentPrice is decimal currentPrice &&
                input.MarketSnapshot.OneMinute.Features?.Vwap is decimal vwap)
            {
                return currentPrice >= vwap;
            }

            return null;
        }

        private static string ResolveTradeResult(string inputResult, decimal netProfitLoss)
        {
            if (!string.IsNullOrWhiteSpace(inputResult))
                return inputResult;

            if (netProfitLoss > 0m)
                return "Win";
            if (netProfitLoss < 0m)
                return "Loss";
            return "Flat";
        }

        private static PostTradeAnalysisRecordSummary ToSummary(PostTradeAnalysisRecord record)
        {
            return new PostTradeAnalysisRecordSummary
            {
                Id = record.Id,
                SourceTradeId = record.SourceTradeId,
                Symbol = record.Symbol,
                Pattern = record.Pattern,
                OpenedUtc = record.OpenedUtc,
                ClosedUtc = record.ClosedUtc,
                GrossProfitLoss = record.GrossProfitLoss,
                CommissionsFees = record.CommissionsFees,
                NetProfitLoss = record.NetProfitLoss,
                MaximumFavorableExcursion = record.MaximumFavorableExcursion,
                MaximumAdverseExcursion = record.MaximumAdverseExcursion,
                HoldingDurationMinutes = record.HoldingDurationMinutes
            };
        }

        private static string Serialize<T>(T value)
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }
    }
}
