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
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class RiskEngine : IRiskEngine
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly RiskSettings _settings;
        private readonly IPositionSizer _positionSizer;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IClock _clock;
        private readonly ILogger<RiskEngine> _logger;

        public RiskEngine(
            IOptions<RiskSettings> settings,
            IPositionSizer positionSizer,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<RiskEngine> logger)
            : this(settings, positionSizer, dbFactory, new SystemClock(), logger)
        {
        }

        public RiskEngine(
            IOptions<RiskSettings> settings,
            IPositionSizer positionSizer,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IClock clock,
            ILogger<RiskEngine> logger)
        {
            _settings = settings?.Value ?? new RiskSettings();
            _positionSizer = positionSizer ?? throw new ArgumentNullException(nameof(positionSizer));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<RiskDecision> EvaluateAsync(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<Trade> todaysCompletedTrades,
            IReadOnlyList<OrderStatusDto> openOrders,
            CancellationToken cancellationToken = default)
        {
            var decidedAt = _clock.UtcNow;
            var reasons = new List<string>();

            ValidateInputs(strategyDecision, accountInfo, currentPositions, todaysCompletedTrades, openOrders, reasons);
            ValidateConfiguredLimits(strategyDecision, accountInfo, currentPositions, todaysCompletedTrades, openOrders, reasons);

            PositionSizingResult sizing = new();
            if (reasons.Count == 0 && strategyDecision.EntryMin.HasValue && strategyDecision.StopPrice.HasValue)
            {
                sizing = _positionSizer.CalculatePositionSize(strategyDecision.EntryMin.Value, strategyDecision.StopPrice.Value, accountInfo, currentPositions);
                reasons.AddRange(sizing.RejectionReasons);
            }

            var approved = reasons.Count == 0;
            var decision = new RiskDecision(
                approved ? RiskDecisionType.Approve : RiskDecisionType.Reject,
                approved ? "Risk checks passed." : string.Join("; ", reasons),
                approved ? sizing.Quantity : 0m,
                approved ? sizing.PositionValue : 0m,
                approved ? sizing.RiskAmount : 0m,
                decidedAt,
                reasons,
                strategyDecision.Context);

            await PersistAsync(strategyDecision, accountInfo, currentPositions, todaysCompletedTrades, openOrders, decision, cancellationToken).ConfigureAwait(false);

            if (!approved)
            {
                _logger.LogInformation("Risk decision rejected for {Symbol}: {Reasons}", strategyDecision?.Symbol, decision.Reason);
            }

            return decision;
        }

        private void ValidateInputs(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<Trade> todaysCompletedTrades,
            IReadOnlyList<OrderStatusDto> openOrders,
            List<string> reasons)
        {
            if (strategyDecision == null)
            {
                reasons.Add("Strategy decision is unavailable.");
                return;
            }

            if (!strategyDecision.Approved) reasons.Add("Strategy decision is not approved.");
            if (strategyDecision.TradeSignal == null) reasons.Add("Trade signal is unavailable.");
            if (!strategyDecision.EntryMin.HasValue || strategyDecision.EntryMin <= 0m) reasons.Add("Entry price is unavailable.");
            if (!strategyDecision.StopPrice.HasValue || strategyDecision.StopPrice <= 0m) reasons.Add("Stop price is unavailable.");
            if (accountInfo == null) reasons.Add("Account information is unavailable.");
            if (currentPositions == null) reasons.Add("Current broker positions are unavailable.");
            if (todaysCompletedTrades == null) reasons.Add("Today's completed trades are unavailable.");
            if (openOrders == null) reasons.Add("Open orders are unavailable.");
        }

        private void ValidateConfiguredLimits(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<Trade> todaysCompletedTrades,
            IReadOnlyList<OrderStatusDto> openOrders,
            List<string> reasons)
        {
            if (strategyDecision == null || accountInfo == null || currentPositions == null || todaysCompletedTrades == null || openOrders == null) return;

            var currentOpenPositionCount = currentPositions.Count(p => p.Quantity != 0m);
            var openOrderCount = openOrders.Count(o => IsOpenOrderStatus(o.Status));
            if (currentOpenPositionCount + openOrderCount >= _settings.MaximumOpenPositions)
            {
                reasons.Add("Maximum open position limit has been reached.");
            }

            var dailyLoss = CalculateRealizedLoss(todaysCompletedTrades);
            if (dailyLoss >= _settings.MaximumDailyLoss)
            {
                reasons.Add("Maximum daily loss limit has been reached.");
            }

            var consecutiveLosses = CountConsecutiveLosses(todaysCompletedTrades);
            if (consecutiveLosses >= _settings.MaximumConsecutiveLosses)
            {
                reasons.Add("Maximum consecutive losses limit has been reached.");
            }

            if (todaysCompletedTrades.Count(t => t.Status == TradeStatus.Closed) >= _settings.MaximumTradesPerDay)
            {
                reasons.Add("Maximum trades per day limit has been reached.");
            }

            if (IsLossCooldownActive(todaysCompletedTrades))
            {
                reasons.Add("Loss cooldown is active after consecutive losses.");
            }
        }

        private static bool IsOpenOrderStatus(string status)
        {
            return status.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
                || status.Equals("PreSubmitted", StringComparison.OrdinalIgnoreCase)
                || status.Equals("PendingSubmit", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Open", StringComparison.OrdinalIgnoreCase);
        }

        private static decimal CalculateRealizedLoss(IReadOnlyList<Trade> trades)
        {
            return trades
                .Where(t => t.Status == TradeStatus.Closed && t.ExitPrice.HasValue)
                .Select(CalculateProfitLoss)
                .Where(pnl => pnl < 0m)
                .Sum(pnl => Math.Abs(pnl));
        }

        private static int CountConsecutiveLosses(IReadOnlyList<Trade> trades)
        {
            var count = 0;
            foreach (var trade in trades
                .Where(t => t.Status == TradeStatus.Closed && t.ClosedAtUtc.HasValue && t.ExitPrice.HasValue)
                .OrderByDescending(t => t.ClosedAtUtc))
            {
                if (CalculateProfitLoss(trade) < 0m) count++;
                else break;
            }

            return count;
        }

        private bool IsLossCooldownActive(IReadOnlyList<Trade> trades)
        {
            if (_settings.LossCooldownAfterConsecutiveLosses <= 0 || _settings.LossCooldownDurationMinutes <= 0) return false;

            var ordered = trades
                .Where(t => t.Status == TradeStatus.Closed && t.ClosedAtUtc.HasValue && t.ExitPrice.HasValue)
                .OrderByDescending(t => t.ClosedAtUtc)
                .ToList();

            if (ordered.Count < _settings.LossCooldownAfterConsecutiveLosses) return false;

            var consecutiveLosses = 0;
            DateTime? latestLossClosedAt = null;
            foreach (var trade in ordered)
            {
                if (CalculateProfitLoss(trade) >= 0m) break;
                consecutiveLosses++;
                latestLossClosedAt ??= trade.ClosedAtUtc;
                if (consecutiveLosses >= _settings.LossCooldownAfterConsecutiveLosses) break;
            }

            if (consecutiveLosses < _settings.LossCooldownAfterConsecutiveLosses || !latestLossClosedAt.HasValue) return false;

            var cooldownEnds = latestLossClosedAt.Value.AddMinutes(_settings.LossCooldownDurationMinutes);
            return _clock.UtcNow < cooldownEnds;
        }

        private static decimal CalculateProfitLoss(Trade trade)
        {
            if (!trade.ExitPrice.HasValue) return 0m;
            return (trade.ExitPrice.Value - trade.EntryPrice) * trade.Quantity;
        }

        private async Task PersistAsync(
            StrategyDecision strategyDecision,
            AccountInfo accountInfo,
            IReadOnlyList<PositionDto> currentPositions,
            IReadOnlyList<Trade> todaysCompletedTrades,
            IReadOnlyList<OrderStatusDto> openOrders,
            RiskDecision decision,
            CancellationToken cancellationToken)
        {
            var payload = new
            {
                strategyDecision,
                accountInfo,
                currentPositions,
                todaysCompletedTrades,
                openOrders,
                riskDecision = decision
            };

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.RiskDecisionRecords.Add(new TradingBot.Persistence.RiskDecisionRecord
            {
                TimestampUtc = decision.DecidedAtUtc,
                DecisionJson = JsonSerializer.Serialize(payload, JsonOptions)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
