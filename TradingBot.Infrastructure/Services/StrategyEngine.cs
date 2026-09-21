using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class StrategyEngine : IStrategyEngine
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly TradingSettings _settings;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IClock _clock;
        private readonly ILogger<StrategyEngine> _logger;

        public StrategyEngine(
            IOptions<TradingSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<StrategyEngine> logger)
            : this(settings, dbFactory, new SystemClock(), logger)
        {
        }

        public StrategyEngine(
            IOptions<TradingSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IClock clock,
            ILogger<StrategyEngine> logger)
        {
            _settings = settings?.Value ?? new TradingSettings();
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<StrategyDecision> EvaluateAsync(
            MarketSnapshot snapshot,
            PatternCandidate pattern,
            AiMarketAnalysisResult analysis,
            AiTradeCriticResult critic,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (critic == null) throw new ArgumentNullException(nameof(critic));

            var evaluatedAt = _clock.UtcNow;
            var reasons = new List<string>();

            ValidateRules(snapshot, analysis, critic, evaluatedAt, reasons);

            var entryMin = analysis.EntryMin;
            var entryMax = analysis.EntryMax;
            var stopPrice = CalculateStopPrice(pattern, analysis, snapshot);
            var takeProfit = CalculateTakeProfitPrice(entryMax, analysis, snapshot);
            var rewardRisk = CalculateRewardRisk(entryMin, entryMax, stopPrice, takeProfit);

            if (entryMin <= 0m || entryMax <= 0m || entryMin > entryMax)
            {
                reasons.Add("Invalid entry zone.");
            }

            if (stopPrice <= 0m || stopPrice >= entryMin)
            {
                reasons.Add("Invalid stop level for long setup.");
            }

            if (takeProfit <= entryMax)
            {
                reasons.Add("Invalid take profit target for long setup.");
            }

            if (rewardRisk == null || rewardRisk < _settings.MinimumRewardRiskRatio)
            {
                reasons.Add($"Reward/risk ratio is below minimum {_settings.MinimumRewardRiskRatio}.");
            }

            var approved = reasons.Count == 0;
            var decision = new StrategyDecision
            {
                Symbol = snapshot.Symbol,
                EvaluatedAtUtc = evaluatedAt,
                Approved = approved,
                TradeSignal = approved
                    ? new TradeSignal(snapshot.Symbol, pattern.Timeframe, TradeAction.Enter, analysis.Confidence, 1m, evaluatedAt)
                    : null,
                RejectionReasons = reasons,
                EntryMin = entryMin,
                EntryMax = entryMax,
                StopPrice = stopPrice,
                TakeProfitPrice = takeProfit,
                ExpectedRewardRiskRatio = rewardRisk
            };

            await PersistAsync(snapshot, pattern, analysis, critic, decision, cancellationToken).ConfigureAwait(false);

            if (!approved)
            {
                _logger.LogInformation("Strategy setup rejected for {Symbol}: {Reasons}", snapshot.Symbol, string.Join("; ", reasons));
            }

            return decision;
        }

        private void ValidateRules(
            MarketSnapshot snapshot,
            AiMarketAnalysisResult analysis,
            AiTradeCriticResult critic,
            DateTime evaluatedAt,
            List<string> reasons)
        {
            if (analysis.Action != AiMarketActions.Buy)
            {
                reasons.Add($"AI action is {analysis.Action}, not BUY.");
            }

            if (analysis.Confidence < _settings.MinimumAiConfidence)
            {
                reasons.Add($"AI confidence {analysis.Confidence} is below minimum {_settings.MinimumAiConfidence}.");
            }

            if (analysis.PatternQuality < _settings.MinimumPatternQuality)
            {
                reasons.Add($"Pattern quality {analysis.PatternQuality} is below minimum {_settings.MinimumPatternQuality}.");
            }

            if (!critic.Approved)
            {
                var criticReason = string.IsNullOrWhiteSpace(critic.Reason) ? "critic rejected setup" : critic.Reason;
                reasons.Add($"AI critic rejected setup: {criticReason}");
            }

            if (snapshot.Spread is decimal spread && spread > _settings.MaximumSpread)
            {
                reasons.Add($"Spread {spread} exceeds maximum {_settings.MaximumSpread}.");
            }

            if (!IsAllowedMarketRegime(analysis.MarketRegime))
            {
                reasons.Add($"Market regime '{analysis.MarketRegime}' is not allowed.");
            }

            if (!IsWithinTradingHours(evaluatedAt))
            {
                reasons.Add("Current New York market time is outside allowed trading hours.");
            }

            if (snapshot.CreatedAtUtc == default || evaluatedAt - ToUtc(snapshot.CreatedAtUtc) > TimeSpan.FromSeconds(Math.Max(1, _settings.MaximumQuoteAgeSeconds)))
            {
                reasons.Add("Market snapshot is stale.");
            }

            var latestOneMinuteCandle = snapshot.OneMinute.RecentCandles
                .OrderByDescending(c => c.TimestampUtc)
                .FirstOrDefault();
            if (latestOneMinuteCandle == null
                || evaluatedAt - ToUtc(latestOneMinuteCandle.TimestampUtc) > TimeSpan.FromSeconds(Math.Max(1, _settings.MaximumCandleAgeSeconds)))
            {
                reasons.Add("Latest one-minute candle is stale or unavailable.");
            }

            if (snapshot.OneMinute.Volatility.Volatility is decimal volatility
                && volatility > _settings.ExtremeVolatilityPercent)
            {
                reasons.Add($"One-minute volatility {volatility} exceeds circuit breaker {_settings.ExtremeVolatilityPercent}.");
            }
        }

        private bool IsAllowedMarketRegime(string marketRegime)
        {
            if (_settings.AllowedMarketRegimes == null || _settings.AllowedMarketRegimes.Length == 0) return true;
            return _settings.AllowedMarketRegimes.Any(allowed =>
                string.Equals(allowed, marketRegime, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsWithinTradingHours(DateTime timestampUtc)
        {
            var eastern = ResolveEasternTimeZone();
            var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(timestampUtc), eastern);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

            var start = new TimeSpan(
                Math.Clamp(_settings.TradingStartHourNewYork, 0, 23),
                Math.Clamp(_settings.TradingStartMinuteNewYork, 0, 59),
                0);
            var end = new TimeSpan(
                Math.Clamp(_settings.TradingEndHourNewYork, 0, 23),
                Math.Clamp(_settings.TradingEndMinuteNewYork, 0, 59),
                0);
            var time = local.TimeOfDay;

            if (start == end) return true;
            if (start < end) return time >= start && time < end;
            return time >= start || time < end;
        }

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }
        }

        private static DateTime ToUtc(DateTime timestamp)
        {
            return timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        }

        private static decimal CalculateStopPrice(PatternCandidate pattern, AiMarketAnalysisResult analysis, MarketSnapshot snapshot)
        {
            if (analysis.InvalidationPrice > 0m)
            {
                return analysis.InvalidationPrice;
            }

            var patternSupport = pattern.RelevantPriceLevels.DefaultIfEmpty().Min();
            if (patternSupport > 0m)
            {
                return patternSupport;
            }

            return snapshot.OneMinute.Volatility.RecentLow ?? snapshot.CurrentPrice ?? 0m;
        }

        private static decimal CalculateTakeProfitPrice(decimal entryMax, AiMarketAnalysisResult analysis, MarketSnapshot snapshot)
        {
            if (entryMax <= 0m) return 0m;

            if (analysis.ExpectedMovePercent > 0m)
            {
                return entryMax * (1m + analysis.ExpectedMovePercent / 100m);
            }

            var resistance = snapshot.OneMinute.SupportResistanceCandidates
                .Where(c => string.Equals(c.Kind, "Resistance", StringComparison.OrdinalIgnoreCase) && c.Price > entryMax)
                .Select(c => c.Price)
                .DefaultIfEmpty()
                .Min();

            return resistance > 0m ? resistance : entryMax;
        }

        private static decimal? CalculateRewardRisk(decimal entryMin, decimal entryMax, decimal stopPrice, decimal takeProfit)
        {
            if (entryMin <= 0m || entryMax <= 0m || stopPrice <= 0m || takeProfit <= 0m) return null;
            if (stopPrice >= entryMin || takeProfit <= entryMax) return null;

            var risk = entryMin - stopPrice;
            var reward = takeProfit - entryMax;
            if (risk <= 0m) return null;

            return reward / risk;
        }

        private async Task PersistAsync(
            MarketSnapshot snapshot,
            PatternCandidate pattern,
            AiMarketAnalysisResult analysis,
            AiTradeCriticResult critic,
            StrategyDecision decision,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                marketSnapshot = snapshot,
                detectedPattern = pattern,
                aiMarketAnalysis = analysis,
                aiCriticAnalysis = critic,
                strategyDecision = decision
            };

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.StrategyDecisionRecords.Add(new TradingBot.Persistence.StrategyDecisionRecord
            {
                TimestampUtc = decision.EvaluatedAtUtc,
                DecisionJson = JsonSerializer.Serialize(payload, JsonOptions)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
