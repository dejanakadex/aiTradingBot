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
            ILogger<ApprovedOrderExecutionBackgroundService> logger)
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
                return;
            }

            try
            {
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
                    return;
                }

                var entryResult = await _orderManager.SubmitLimitBuyAsync(entry, plan.RiskDecision, cancellationToken).ConfigureAwait(false);
                if (!entryResult.Submitted)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.OrderExecution,
                        TradingPipelineActivityState.Rejected,
                        $"Entry order rejected: {entryResult.Message}",
                        plan.StrategyDecision.Symbol,
                        plan.Pattern.PatternType.ToString());

                    _logger.LogWarning("Entry limit buy was not submitted for {Symbol}: {Message}", plan.StrategyDecision.Symbol, entryResult.Message);
                    return;
                }

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
