using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Background
{
    public sealed class ApprovedOrderExecutionBackgroundService : BackgroundService
    {
        private readonly ITradePipelineChannel _pipelineChannel;
        private readonly IOrderManager _orderManager;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IOperatingModeService _operatingModeService;
        private readonly IExitManagementService _exitManagementService;
        private readonly ITradingPipelineStatusService? _pipelineStatusService;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ExitStrategySettings _exitStrategySettings;
        private readonly TradingSettings _tradingSettings;
        private readonly ILogger<ApprovedOrderExecutionBackgroundService> _logger;
        private readonly IPortfolioRiskService? _portfolioRiskService;
        private readonly ISignalArbitrationService? _signalArbitrationService;
        private readonly ConcurrentDictionary<string, byte> _submittedPlans = new();

        public ApprovedOrderExecutionBackgroundService(
            ITradePipelineChannel pipelineChannel,
            IOrderManager orderManager,
            ITradingEngineStatusService statusService,
            IOperatingModeService operatingModeService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOptions<TradingSettings> tradingSettings,
            ILogger<ApprovedOrderExecutionBackgroundService> logger)
            : this(
                pipelineChannel,
                orderManager,
                statusService,
                operatingModeService,
                new NoopExitManagementService(),
                null,
                dbFactory,
                Microsoft.Extensions.Options.Options.Create(new ExitStrategySettings { Mode = ExitStrategyMode.FixedBracket }),
                tradingSettings,
                logger)
        {
        }

        public ApprovedOrderExecutionBackgroundService(
            ITradePipelineChannel pipelineChannel,
            IOrderManager orderManager,
            ITradingEngineStatusService statusService,
            IOperatingModeService operatingModeService,
            IExitManagementService exitManagementService,
            ITradingPipelineStatusService? pipelineStatusService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOptions<ExitStrategySettings> exitStrategySettings,
            IOptions<TradingSettings> tradingSettings,
            ILogger<ApprovedOrderExecutionBackgroundService> logger,
            IPortfolioRiskService? portfolioRiskService = null,
            ISignalArbitrationService? signalArbitrationService = null)
        {
            _pipelineChannel = pipelineChannel;
            _orderManager = orderManager;
            _statusService = statusService;
            _operatingModeService = operatingModeService;
            _exitManagementService = exitManagementService;
            _pipelineStatusService = pipelineStatusService;
            _dbFactory = dbFactory;
            _exitStrategySettings = exitStrategySettings.Value;
            _tradingSettings = tradingSettings.Value;
            _logger = logger;
            _portfolioRiskService = portfolioRiskService;
            _signalArbitrationService = signalArbitrationService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Approved order execution service started");
            try
            {
                await foreach (var plan in _pipelineChannel.ApprovedTradePlanReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    await ExecutePlanAsync(plan, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Approved order execution service cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical failure in approved order execution service");
                _statusService.SetState(TradingEngineState.Faulted, false, "Approved order execution service failed.", new[] { ex.Message });
            }
            finally
            {
                _logger.LogInformation("Approved order execution service stopped");
            }
        }

        private async Task ExecutePlanAsync(ApprovedTradePlan plan, CancellationToken cancellationToken)
        {
            if (_statusService.Current.State != TradingEngineState.Ready || !_statusService.Current.TradingEnabled || !_tradingSettings.Enabled)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Rejected,
                    "Approved plan skipped because trading is not ready/enabled.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                _logger.LogWarning(
                    "Approved trade plan skipped because trading is not ready/enabled. State={State}, engineTrading={EngineTrading}, appTrading={AppTrading}",
                    _statusService.Current.State,
                    _statusService.Current.TradingEnabled,
                    _tradingSettings.Enabled);
                await RejectPlanAsync(plan, "Trading was no longer ready or enabled before submission.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (plan.StrategyDecision.EntryMin == null || plan.StrategyDecision.StopPrice == null || plan.StrategyDecision.TakeProfitPrice == null)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Rejected,
                    "Approved plan skipped because entry/stop/target is incomplete.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                _logger.LogWarning("Approved trade plan skipped because entry/stop/target is incomplete for {Symbol}", plan.StrategyDecision.Symbol);
                await RejectPlanAsync(plan, "Entry, stop or target was incomplete before submission.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var key = $"{plan.StrategyDecision.Symbol}:{plan.Pattern.PatternType}:{plan.Pattern.DetectedAtUtc:O}:{plan.RiskDecision.ApprovedQuantity}";
            if (!_submittedPlans.TryAdd(key, 0))
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Rejected,
                    "Duplicate approved trade plan prevented.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                _logger.LogWarning("Duplicate approved trade plan prevented for {Key}", key);
                await RejectPlanAsync(plan, "Duplicate approved trade plan was prevented.", cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                if (_signalArbitrationService != null)
                {
                    if (!plan.ArbitrationId.HasValue)
                    {
                        await RejectPlanAsync(plan, "Approved plan is missing its arbitration identity.", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var claim = await _signalArbitrationService.ClaimForExecutionAsync(plan.ArbitrationId.Value, cancellationToken).ConfigureAwait(false);
                    if (!claim.Approved)
                    {
                        await RejectPlanAsync(plan, $"Signal arbitration execution claim rejected: {claim.Reason}", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Active,
                    $"Preparing order workflow for {plan.StrategyDecision.Symbol}.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                var now = DateTime.UtcNow;
                var entry = new OrderRequest(plan.StrategyDecision.Symbol, OrderSide.Buy, OrderType.Limit, plan.RiskDecision.ApprovedQuantity, plan.StrategyDecision.EntryMin.Value, now);
                var stop = new OrderRequest(plan.StrategyDecision.Symbol, OrderSide.Sell, OrderType.Stop, plan.RiskDecision.ApprovedQuantity, plan.StrategyDecision.StopPrice.Value, now);
                var target = new OrderRequest(plan.StrategyDecision.Symbol, OrderSide.Sell, OrderType.Limit, plan.RiskDecision.ApprovedQuantity, plan.StrategyDecision.TakeProfitPrice.Value, now);

                var mode = _operatingModeService.CurrentMode;
                if (mode == TradingOperatingMode.AnalysisOnly)
                {
                    await PersistHypotheticalTradeAsync(plan, entry, stop, target, "AnalysisOnly mode: broker order submission skipped.", cancellationToken).ConfigureAwait(false);
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.OrderExecution,
                        TradingPipelineActivityState.Completed,
                        "AnalysisOnly: hypothetical trade stored; broker order blocked.",
                        plan.StrategyDecision.Symbol,
                        plan.Pattern.PatternType.ToString());

                    _logger.LogInformation("Stored hypothetical trade for {Symbol}; mode={Mode}", plan.StrategyDecision.Symbol, mode);
                    await ReleaseReservationAsync(plan, "Analysis-only hypothetical trade completed without broker exposure.", cancellationToken).ConfigureAwait(false);
                    if (_signalArbitrationService != null && plan.ArbitrationId.HasValue)
                        await _signalArbitrationService.CompleteAnalysisOnlyAsync(plan.ArbitrationId.Value, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (_exitStrategySettings.Mode == ExitStrategyMode.FixedBracket)
                {
                    var result = await _orderManager.SubmitBracketOrderAsync(entry, stop, target, plan.RiskDecision, cancellationToken).ConfigureAwait(false);
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.OrderExecution,
                        result.Submitted ? TradingPipelineActivityState.Completed : TradingPipelineActivityState.Rejected,
                        result.Submitted ? $"Bracket order submitted: {result.Status}." : $"Bracket order rejected: {result.Message}",
                        plan.StrategyDecision.Symbol,
                        plan.Pattern.PatternType.ToString());

                    _logger.LogInformation("Submitted fixed bracket trade plan for {Symbol}; mode={Mode}, brokerOrderId={BrokerOrderId}, status={Status}", plan.StrategyDecision.Symbol, mode, result.BrokerOrderId, result.Status);
                    if (_signalArbitrationService != null && plan.ArbitrationId.HasValue)
                        await _signalArbitrationService.RecordSubmissionAsync(plan.ArbitrationId.Value, result, cancellationToken).ConfigureAwait(false);
                    if (result.Submitted || result.Status == OrderStatus.PendingBrokerConfirmation)
                    {
                        await CommitReservationAsync(plan, result.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReleaseReservationAsync(plan, $"Bracket order was not accepted: {result.Message}", cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }

                var entryResult = await _orderManager.SubmitLimitBuyAsync(entry, plan.RiskDecision, cancellationToken).ConfigureAwait(false);
                if (_signalArbitrationService != null && plan.ArbitrationId.HasValue)
                    await _signalArbitrationService.RecordSubmissionAsync(plan.ArbitrationId.Value, entryResult, cancellationToken).ConfigureAwait(false);
                if (!entryResult.Submitted)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.OrderExecution,
                        TradingPipelineActivityState.Rejected,
                        $"Entry order rejected: {entryResult.Message}",
                        plan.StrategyDecision.Symbol,
                        plan.Pattern.PatternType.ToString());

                    _logger.LogWarning("Entry limit buy was not submitted for {Symbol}: {Message}", plan.StrategyDecision.Symbol, entryResult.Message);
                    if (entryResult.Status == OrderStatus.PendingBrokerConfirmation)
                    {
                        await CommitReservationAsync(plan, entryResult.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await ReleaseReservationAsync(plan, $"Entry order was not accepted: {entryResult.Message}", cancellationToken).ConfigureAwait(false);
                    }
                    return;
                }

                await CommitReservationAsync(plan, entryResult.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                await _exitManagementService.RegisterApprovedEntryAsync(plan, entryResult, cancellationToken).ConfigureAwait(false);
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Completed,
                    $"Entry order submitted: {entryResult.Status}.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                _logger.LogInformation("Submitted entry limit buy for trailing exit plan {Symbol}; mode={Mode}, brokerOrderId={BrokerOrderId}, status={Status}", plan.StrategyDecision.Symbol, mode, entryResult.BrokerOrderId, entryResult.Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Approved trade plan execution canceled during shutdown for {Symbol}", plan.StrategyDecision.Symbol);
                throw;
            }
            catch (Exception ex)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Error,
                    "Order workflow failed.",
                    plan.StrategyDecision.Symbol,
                    plan.Pattern.PatternType.ToString());

                _logger.LogError(ex, "Failed to submit approved trade plan for {Symbol}", plan.StrategyDecision.Symbol);
            }
        }

        private Task CommitReservationAsync(ApprovedTradePlan plan, string? brokerOrderId, CancellationToken cancellationToken)
        {
            return _portfolioRiskService != null && plan.RiskDecision.ReservationId.HasValue
                ? _portfolioRiskService.CommitAsync(plan.RiskDecision.ReservationId.Value, brokerOrderId, cancellationToken)
                : Task.CompletedTask;
        }

        private Task ReleaseReservationAsync(ApprovedTradePlan plan, string reason, CancellationToken cancellationToken)
        {
            return _portfolioRiskService != null && plan.RiskDecision.ReservationId.HasValue
                ? _portfolioRiskService.ReleaseAsync(plan.RiskDecision.ReservationId.Value, reason, cancellationToken)
                : Task.CompletedTask;
        }

        private async Task RejectPlanAsync(ApprovedTradePlan plan, string reason, CancellationToken cancellationToken)
        {
            if (_signalArbitrationService != null && plan.ArbitrationId.HasValue)
            {
                await _signalArbitrationService.RejectAsync(plan.ArbitrationId.Value, reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            await ReleaseReservationAsync(plan, reason, cancellationToken).ConfigureAwait(false);
        }

        private async Task PersistHypotheticalTradeAsync(
            ApprovedTradePlan plan,
            OrderRequest entry,
            OrderRequest stop,
            OrderRequest target,
            string reason,
            CancellationToken cancellationToken)
        {
            var payload = JsonSerializer.Serialize(new
            {
                recordType = "HypotheticalTrade",
                operatingMode = _operatingModeService.CurrentMode.ToString(),
                reason,
                approvedAtUtc = plan.ApprovedAtUtc,
                symbol = plan.StrategyDecision.Symbol,
                pattern = plan.Pattern.PatternType.ToString(),
                aiConfidence = plan.AiAnalysis.Confidence,
                riskQuantity = plan.RiskDecision.ApprovedQuantity,
                entry,
                stop,
                target,
                strategyDecision = plan.StrategyDecision,
                riskDecision = plan.RiskDecision
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.StrategyDecisionRecords.Add(new TradingBot.Persistence.StrategyDecisionRecord
            {
                TimestampUtc = DateTime.UtcNow,
                DecisionJson = payload
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private sealed class NoopExitManagementService : IExitManagementService
        {
            public Task RegisterApprovedEntryAsync(ApprovedTradePlan plan, ManagedOrderResult entryOrder, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task ProcessMarketCandleAsync(Candle candle, CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }

            public Task RestoreAsync(CancellationToken cancellationToken = default)
            {
                return Task.CompletedTask;
            }
        }
    }
}
