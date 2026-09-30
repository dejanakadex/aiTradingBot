using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ScalpingExecutionGate : IScalpingExecutionGate
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly ILatestMarketDataService _latestMarketData;
        private readonly IOperatingModeService _operatingModeService;
        private readonly IAccountService _accountService;
        private readonly IPositionService _positionService;
        private readonly IOrderExecutionService _orderExecutionService;
        private readonly IPortfolioRiskService _portfolioRiskService;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ScalpingExecutionSettings _settings;
        private readonly TradingSettings _tradingSettings;
        private readonly RiskSettings _riskSettings;
        private readonly IbkrSettings _ibkrSettings;
        private readonly IClock _clock;
        private readonly ILogger<ScalpingExecutionGate> _logger;

        public ScalpingExecutionGate(
            ILatestMarketDataService latestMarketData,
            IOperatingModeService operatingModeService,
            IAccountService accountService,
            IPositionService positionService,
            IOrderExecutionService orderExecutionService,
            IPortfolioRiskService portfolioRiskService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOptions<ScalpingExecutionSettings> settings,
            IOptions<TradingSettings> tradingSettings,
            IOptions<RiskSettings> riskSettings,
            IOptions<IbkrSettings> ibkrSettings,
            IClock clock,
            ILogger<ScalpingExecutionGate> logger)
        {
            _latestMarketData = latestMarketData;
            _operatingModeService = operatingModeService;
            _accountService = accountService;
            _positionService = positionService;
            _orderExecutionService = orderExecutionService;
            _portfolioRiskService = portfolioRiskService;
            _dbFactory = dbFactory;
            _settings = settings.Value;
            _tradingSettings = tradingSettings.Value;
            _riskSettings = riskSettings.Value;
            _ibkrSettings = ibkrSettings.Value;
            _clock = clock;
            _logger = logger;
        }

        public async Task<ScalpingExecutionDecision> EvaluateAsync(ApprovedTradePlan plan, CancellationToken cancellationToken = default)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var started = Stopwatch.GetTimestamp();
            var now = _clock.UtcNow;
            var reasons = new List<string>();
            ScalpingExecutionDecision decision;

            if (!_settings.Enabled)
            {
                reasons.Add("Scalping execution gate is disabled; fail-closed submission is required.");
                decision = ScalpingExecutionDecision.Reject(now, reasons[0], reasons);
                await PersistAsync(plan, decision, Stopwatch.GetElapsedTime(started), cancellationToken).ConfigureAwait(false);
                return decision;
            }

            ValidatePlanAge(plan, now, reasons);
            var quote = _latestMarketData.Get(plan.Context.InstrumentId) ?? _latestMarketData.Get(plan.StrategyDecision.Symbol);
            ValidateQuote(quote, now, reasons);

            if (quote?.Bid is not > 0m || quote.Ask is not > 0m || reasons.Count > 0)
            {
                decision = ScalpingExecutionDecision.Reject(now, string.Join(" ", reasons), reasons);
                await PersistAsync(plan, decision, Stopwatch.GetElapsedTime(started), cancellationToken).ConfigureAwait(false);
                return decision;
            }

            var bid = quote.Bid.Value;
            var ask = quote.Ask.Value;
            var quoteAsOf = new[] { quote.BidTimeUtc!.Value, quote.AskTimeUtc!.Value }.Min();
            var mid = (bid + ask) / 2m;
            var spreadBps = (ask - bid) / mid * 10_000m;
            if (spreadBps > _settings.MaximumSpreadBps)
                reasons.Add($"Spread {spreadBps:F4} bps exceeds maximum {_settings.MaximumSpreadBps:F4} bps.");

            var entryMin = plan.StrategyDecision.EntryMin ?? 0m;
            var entryMax = plan.StrategyDecision.EntryMax ?? entryMin;
            var target = plan.StrategyDecision.TakeProfitPrice ?? 0m;
            if (entryMin <= 0m || entryMax < entryMin || target <= 0m)
                reasons.Add("Approved plan has invalid entry range or target.");

            var passivePrice = Math.Max(bid, entryMin);
            var marketablePrice = ask * (1m + _settings.MarketableLimitOffsetBps / 10_000m);
            var aiEdgeBps = plan.AiAnalysis.ExpectedMovePercent * 100m;
            var estimates = new[]
            {
                Estimate(ScalpingOrderPolicy.PassiveLimit, passivePrice, target, aiEdgeBps, spreadBps, _settings.PassiveLimitSlippagePerSideBps),
                Estimate(ScalpingOrderPolicy.MarketableLimit, marketablePrice, target, aiEdgeBps, spreadBps, _settings.MarketableLimitSlippagePerSideBps),
                Estimate(ScalpingOrderPolicy.Market, ask, target, aiEdgeBps, spreadBps, _settings.MarketSlippagePerSideBps)
            };
            var selected = estimates.Single(x => x.Policy == _settings.EntryPolicy);
            if (selected.EntryPrice < entryMin || selected.EntryPrice > entryMax)
                reasons.Add($"Selected {_settings.EntryPolicy} entry {selected.EntryPrice:F4} is outside approved range {entryMin:F4}-{entryMax:F4}.");
            if (selected.ExpectedGrossEdgeBps <= 0m)
                reasons.Add("Expected gross edge is not positive.");
            if (selected.ExpectedNetEdgeBps < _settings.MinimumNetEdgeBps)
                reasons.Add($"Expected net edge {selected.ExpectedNetEdgeBps:F4} bps is below minimum {_settings.MinimumNetEdgeBps:F4} bps.");

            var expectedHoldingSeconds = ResolveExpectedHoldingSeconds(plan);
            if (expectedHoldingSeconds <= 0)
                reasons.Add("Expected holding time in seconds is unavailable.");

            if (_operatingModeService.CurrentMode != TradingOperatingMode.AnalysisOnly)
            {
                await ValidateLiveRiskAsync(plan, selected.EntryPrice, reasons, cancellationToken).ConfigureAwait(false);
            }

            var orderType = selected.Policy == ScalpingOrderPolicy.Market ? OrderType.Market : OrderType.Limit;
            decision = new ScalpingExecutionDecision(
                reasons.Count == 0,
                reasons.Count == 0 ? "Final quote, risk, latency and edge checks passed." : string.Join(" ", reasons),
                now,
                quoteAsOf,
                bid,
                ask,
                plan.RiskDecision.ApprovedQuantity,
                expectedHoldingSeconds,
                selected.Policy,
                orderType,
                orderType == OrderType.Market ? null : selected.EntryPrice,
                selected,
                estimates,
                reasons);

            await PersistAsync(plan, decision, Stopwatch.GetElapsedTime(started), cancellationToken).ConfigureAwait(false);
            return decision;
        }

        private void ValidatePlanAge(ApprovedTradePlan plan, DateTime now, List<string> reasons)
        {
            if (plan.RiskDecision.Decision != RiskDecisionType.Approve || plan.RiskDecision.ApprovedQuantity <= 0m)
                reasons.Add("Risk decision is no longer approved.");
            if (plan.ApprovedAtUtc > now || now - plan.ApprovedAtUtc > TimeSpan.FromMilliseconds(_settings.MaximumApprovedPlanAgeMilliseconds))
                reasons.Add("Approved plan exceeded the execution latency budget.");
            if (plan.RiskDecision.DecidedAtUtc > now || now - plan.RiskDecision.DecidedAtUtc > TimeSpan.FromMilliseconds(_settings.MaximumRiskDecisionAgeMilliseconds))
                reasons.Add("Risk decision exceeded the execution latency budget.");
        }

        private void ValidateQuote(TradingBot.Application.DTOs.LatestMarketDataSnapshot? quote, DateTime now, List<string> reasons)
        {
            if (quote?.Bid is not > 0m || quote.Ask is not > 0m || !quote.BidTimeUtc.HasValue || !quote.AskTimeUtc.HasValue)
            {
                reasons.Add("Complete bid/ask quote is unavailable.");
                return;
            }
            if (quote.Ask < quote.Bid) reasons.Add("Ask is below bid.");
            var oldest = new[] { quote.BidTimeUtc.Value, quote.AskTimeUtc.Value }.Min();
            if (oldest > now || now - oldest > TimeSpan.FromMilliseconds(_settings.MaximumQuoteAgeMilliseconds))
                reasons.Add("Bid/ask quote is stale or from the future.");
            if ((quote.BidTimeUtc.Value - quote.AskTimeUtc.Value).Duration() > TimeSpan.FromMilliseconds(_settings.MaximumBidAskSkewMilliseconds))
                reasons.Add("Bid/ask timestamps exceed the permitted skew.");
        }

        private ExecutionCostEstimate Estimate(
            ScalpingOrderPolicy policy,
            decimal entryPrice,
            decimal target,
            decimal aiEdgeBps,
            decimal spreadBps,
            decimal slippagePerSideBps)
        {
            var targetEdgeBps = entryPrice > 0m ? (target - entryPrice) / entryPrice * 10_000m : 0m;
            var grossEdge = aiEdgeBps > 0m ? Math.Min(aiEdgeBps, targetEdgeBps) : 0m;
            var commission = _settings.CommissionPerSideBps * 2m;
            var slippage = slippagePerSideBps * 2m;
            var total = spreadBps + commission + slippage + _settings.SafetyBufferBps;
            return new ExecutionCostEstimate(policy, entryPrice, spreadBps, commission, slippage, _settings.SafetyBufferBps, total, grossEdge, grossEdge - total);
        }

        private int ResolveExpectedHoldingSeconds(ApprovedTradePlan plan)
        {
            var aiSeconds = plan.AiAnalysis.ExpectedHorizonMinutes > 0
                ? checked(plan.AiAnalysis.ExpectedHorizonMinutes * 60)
                : 0;
            var instrument = _tradingSettings.GetConfiguredInstruments().FirstOrDefault(x =>
                x.InstrumentId.Equals(plan.Context.InstrumentId, StringComparison.OrdinalIgnoreCase)
                || x.Symbol.Equals(plan.StrategyDecision.Symbol, StringComparison.OrdinalIgnoreCase));
            return instrument?.MaximumHoldingSeconds is > 0
                ? (aiSeconds > 0 ? Math.Min(aiSeconds, instrument.MaximumHoldingSeconds.Value) : instrument.MaximumHoldingSeconds.Value)
                : aiSeconds;
        }

        private async Task ValidateLiveRiskAsync(ApprovedTradePlan plan, decimal entryPrice, List<string> reasons, CancellationToken cancellationToken)
        {
            try
            {
                var accountId = _operatingModeService.CurrentMode == TradingOperatingMode.PaperTrading
                    ? _ibkrSettings.PaperAccountId
                    : _ibkrSettings.AccountId;
                if (string.IsNullOrWhiteSpace(accountId))
                {
                    reasons.Add("Expected broker account is not configured.");
                    return;
                }

                var account = await _accountService.GetAccountInfoAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (!account.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase))
                    reasons.Add("Broker account changed after risk approval.");
                var positions = (await _positionService.GetPositionsAsync(accountId, cancellationToken).ConfigureAwait(false)).ToArray();
                var orders = (await _orderExecutionService.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false)).ToArray();
                if (orders.Any(x => IsOpen(x.Status)
                    && string.Equals(x.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Symbol, plan.StrategyDecision.Symbol, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add("A broker BUY order for this symbol appeared after risk approval.");

                var riskState = await _portfolioRiskService.GetStateAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (!plan.RiskDecision.ReservationId.HasValue
                    || !riskState.ActiveReservations.Any(x => x.Id == plan.RiskDecision.ReservationId.Value
                        && x.Quantity >= plan.RiskDecision.ApprovedQuantity))
                    reasons.Add("The approved portfolio reservation is missing, expired or too small.");

                var positionGross = positions.Sum(x => Math.Abs(x.Quantity * x.AveragePrice));
                var positionNet = positions.Sum(x => x.Quantity * x.AveragePrice);
                var totalGross = positionGross + riskState.ReservedGrossExposure;
                var totalNet = positionNet + riskState.ReservedNetExposure;
                var instrumentExposure = positions.Where(x => x.Symbol.Equals(plan.StrategyDecision.Symbol, StringComparison.OrdinalIgnoreCase))
                    .Sum(x => Math.Abs(x.Quantity * x.AveragePrice)) + plan.RiskDecision.ApprovedQuantity * entryPrice;
                if (totalGross > _riskSettings.MaximumGrossExposure) reasons.Add("Current gross exposure exceeds the approved limit.");
                if (Math.Abs(totalNet) > _riskSettings.MaximumNetExposure) reasons.Add("Current net exposure exceeds the approved limit.");
                if (instrumentExposure > _riskSettings.MaximumInstrumentExposure) reasons.Add("Current instrument exposure exceeds the approved limit.");
                if (riskState.ReservedGrossExposure > account.BuyingPower) reasons.Add("Current buying power no longer covers active reservations.");
                if (totalGross > account.NetLiquidation * _riskSettings.MaximumLeverage) reasons.Add("Current leverage no longer covers the approved order.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Final broker risk refresh failed for {Symbol}.", plan.StrategyDecision.Symbol);
                reasons.Add("Final broker risk refresh failed.");
            }
        }

        private async Task PersistAsync(ApprovedTradePlan plan, ScalpingExecutionDecision decision, TimeSpan evaluationDuration, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.ScalpingExecutionDecisionRecords.Add(new TradingBot.Persistence.ScalpingExecutionDecisionRecord
            {
                SignalId = plan.Context.SignalId,
                InstrumentId = plan.Context.InstrumentId,
                StrategyId = plan.Context.StrategyId,
                Symbol = plan.StrategyDecision.Symbol,
                EvaluatedAtUtc = decision.EvaluatedAtUtc,
                QuoteAsOfUtc = decision.QuoteAsOfUtc == default ? null : decision.QuoteAsOfUtc,
                Approved = decision.Approved,
                SelectedPolicy = decision.SelectedPolicy.ToString(),
                Bid = decision.Bid,
                Ask = decision.Ask,
                Quantity = decision.Quantity,
                ExpectedHoldingSeconds = decision.ExpectedHoldingSeconds,
                ExpectedGrossEdgeBps = decision.SelectedEstimate.ExpectedGrossEdgeBps,
                EstimatedCostBps = decision.SelectedEstimate.TotalCostBps,
                ExpectedNetEdgeBps = decision.SelectedEstimate.ExpectedNetEdgeBps,
                Reason = decision.Reason,
                DecisionJson = JsonSerializer.Serialize(new { decision, evaluationDurationMilliseconds = evaluationDuration.TotalMilliseconds }, JsonOptions)
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private static bool IsOpen(string status) =>
            status.Equals("New", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PreSubmitted", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("PendingBrokerConfirmation", StringComparison.OrdinalIgnoreCase);

    }
}
