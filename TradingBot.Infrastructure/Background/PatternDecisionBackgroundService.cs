using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Background
{
    public sealed class PatternDecisionBackgroundService : BackgroundService
    {
        private readonly ITradingEventBus _eventBus;
        private readonly ITradePipelineChannel _pipelineChannel;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IPatternQualityGate _patternQualityGate;
        private readonly ITradingPipelineStatusService? _pipelineStatusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly TradingSettings _tradingSettings;
        private readonly IbkrSettings _ibkrSettings;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IClock _clock;
        private readonly ILogger<PatternDecisionBackgroundService> _logger;
        private readonly ICandidateResearchService? _candidateResearchService;
        private readonly ConcurrentDictionary<string, byte> _processedPatterns = new();

        public PatternDecisionBackgroundService(
            ITradingEventBus eventBus,
            ITradePipelineChannel pipelineChannel,
            ITradingEngineStatusService statusService,
            IPatternQualityGate patternQualityGate,
            IServiceProvider serviceProvider,
            IOptions<TradingSettings> tradingSettings,
            IOptions<IbkrSettings> ibkrSettings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IClock clock,
            ILogger<PatternDecisionBackgroundService> logger,
            ITradingPipelineStatusService? pipelineStatusService = null,
            ICandidateResearchService? candidateResearchService = null)
        {
            _eventBus = eventBus;
            _pipelineChannel = pipelineChannel;
            _statusService = statusService;
            _patternQualityGate = patternQualityGate;
            _pipelineStatusService = pipelineStatusService;
            _serviceProvider = serviceProvider;
            _tradingSettings = tradingSettings.Value;
            _ibkrSettings = ibkrSettings.Value;
            _dbFactory = dbFactory;
            _clock = clock;
            _logger = logger;
            _candidateResearchService = candidateResearchService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Pattern decision service started");
            try
            {
                await foreach (var pattern in _eventBus.PatternCandidateReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    await ProcessPatternAsync(pattern, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Pattern decision service cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical failure in pattern decision service");
                _statusService.SetState(TradingEngineState.Faulted, false, "Pattern decision service failed.", new[] { ex.Message });
            }
            finally
            {
                _logger.LogInformation("Pattern decision service stopped");
            }
        }

        internal async Task ProcessPatternAsync(PatternCandidate pattern, CancellationToken cancellationToken)
        {
            var key = pattern.PatternKey;
            if (!_processedPatterns.TryAdd(key, 0))
            {
                _logger.LogDebug("Skipping duplicate pattern {Key}", key);
                return;
            }

            if (!IsWithinTradingHours(_clock.UtcNow))
            {
                var reason = "Outside configured New York trading hours; AI analysis skipped.";
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Rejected,
                    reason,
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                _logger.LogInformation(
                    "Skipping OpenAI analysis for {Pattern} {Symbol}; {Reason}",
                    pattern.PatternType,
                    pattern.Symbol,
                    reason);
                await PersistPipelineDecisionAsync("PatternRejectedBeforeAi", pattern, reason, cancellationToken).ConfigureAwait(false);
                await MarkBlockedAsync(pattern, "TradingHours", new[] { reason }, cancellationToken).ConfigureAwait(false);
                return;
            }

            var patternAge = _clock.UtcNow - ToUtc(pattern.DetectedAtUtc);
            var maximumPatternAge = TimeSpan.FromSeconds(Math.Max(1, _tradingSettings.MaximumCandleAgeSeconds));
            if (patternAge > maximumPatternAge)
            {
                var reason = $"Pattern is stale ({FormatAge(patternAge)} old); AI analysis skipped.";
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Rejected,
                    reason,
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                _logger.LogInformation(
                    "Skipping OpenAI analysis for {Pattern} {Symbol}; patternAge={PatternAge}, maximumAge={MaximumAge}",
                    pattern.PatternType,
                    pattern.Symbol,
                    patternAge,
                    maximumPatternAge);
                await PersistPipelineDecisionAsync("PatternRejectedBeforeAi", pattern, reason, cancellationToken).ConfigureAwait(false);
                await MarkBlockedAsync(pattern, "Freshness", new[] { reason }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_statusService.Current.State != TradingEngineState.Ready || !_statusService.Current.TradingEnabled || !_tradingSettings.Enabled)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Rejected,
                    "Trading engine is not ready/enabled.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                _logger.LogInformation("Skipping pattern {Pattern} for {Symbol}; engineState={State}, engineTrading={EngineTrading}, appTrading={AppTrading}",
                    pattern.PatternType,
                    pattern.Symbol,
                    _statusService.Current.State,
                    _statusService.Current.TradingEnabled,
                    _tradingSettings.Enabled);
                await MarkBlockedAsync(pattern, "TradingEngine", new[] { "Trading engine is not ready/enabled." }, cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var snapshotService = scope.ServiceProvider.GetRequiredService<IMarketSnapshotService>();
                var aiAnalyzer = scope.ServiceProvider.GetRequiredService<IAiMarketAnalyzer>();
                var aiCritic = scope.ServiceProvider.GetRequiredService<IAiTradeCritic>();
                var strategyEngine = scope.ServiceProvider.GetRequiredService<IStrategyEngine>();
                var riskEngine = scope.ServiceProvider.GetRequiredService<IRiskEngine>();
                var accountService = scope.ServiceProvider.GetService<IAccountService>();
                var positionService = scope.ServiceProvider.GetService<IPositionService>();
                var orderExecutionService = scope.ServiceProvider.GetRequiredService<IOrderExecutionService>();

                if (accountService == null || positionService == null)
                {
                    _statusService.SetState(TradingEngineState.Degraded, false, "Broker account/position services are not available. Trading remains disabled.");
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.PatternAnalysis,
                        TradingPipelineActivityState.Error,
                        "Broker account/position services are unavailable.",
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    _logger.LogWarning("Skipping pattern {Pattern} for {Symbol}; account or position broker service is not registered", pattern.PatternType, pattern.Symbol);
                    await MarkBlockedAsync(pattern, "BrokerAvailability", new[] { "Broker account/position services are unavailable." }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.FetchingMarketContext,
                    TradingPipelineActivityState.Active,
                    $"Building market snapshot for {pattern.Symbol}.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                var snapshot = await snapshotService.BuildSnapshotAsync(pattern.Symbol, cancellationToken: cancellationToken).ConfigureAwait(false);

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Active,
                    $"Checking {pattern.PatternType} quality gate.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                if (!_patternQualityGate.TryCreateTradeSetup(pattern, snapshot, out var setupCandidate, out var gateReasons))
                {
                    var reason = string.Join("; ", gateReasons);
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.PatternAnalysis,
                        TradingPipelineActivityState.Rejected,
                        $"Rejected before AI: {reason}",
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    _logger.LogInformation(
                        "Pattern quality gate rejected {Pattern} {Symbol}: {Reason}",
                        pattern.PatternType,
                        pattern.Symbol,
                        reason);
                    await PersistPipelineDecisionAsync("PatternRejectedBeforeAi", pattern, reason, cancellationToken, setupCandidate: setupCandidate).ConfigureAwait(false);
                    await MarkBlockedAsync(pattern, "PatternQualityGate", gateReasons, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await PersistPipelineDecisionAsync("TradeSetupCandidateSentToAi", pattern, "TradeSetupCandidate passed deterministic pattern quality gate.", cancellationToken, setupCandidate: setupCandidate).ConfigureAwait(false);

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.AiAnalyzer,
                    TradingPipelineActivityState.Active,
                    $"AI analyzer reviewing {setupCandidate.Pattern.PatternType}.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                var analysis = await aiAnalyzer.AnalyzeAsync(snapshot, setupCandidate.Pattern, cancellationToken).ConfigureAwait(false);

                if (!ShouldCallCritic(analysis, out var criticSkipReason))
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.AiAnalyzer,
                        TradingPipelineActivityState.Rejected,
                        criticSkipReason,
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    _logger.LogInformation(
                        "Skipping AI critic for {Pattern} {Symbol}: {Reason}",
                        pattern.PatternType,
                        pattern.Symbol,
                        criticSkipReason);
                    await PersistPipelineDecisionAsync("CriticSkipped", pattern, criticSkipReason, cancellationToken, analysis, setupCandidate).ConfigureAwait(false);
                    await MarkBlockedAsync(pattern, "AiAnalyzer", new[] { criticSkipReason }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.AiCritic,
                    TradingPipelineActivityState.Active,
                    $"AI critic reviewing BUY setup for {pattern.Symbol}.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                var critic = await aiCritic.CritiqueAsync(snapshot, pattern, analysis, cancellationToken).ConfigureAwait(false);

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.StrategyAndRisk,
                    TradingPipelineActivityState.Active,
                    "Checking strategy rules.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                var strategy = await strategyEngine.EvaluateAsync(snapshot, pattern, analysis, critic, cancellationToken).ConfigureAwait(false);

                if (!strategy.Approved || strategy.TradeSignal == null)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.StrategyAndRisk,
                        TradingPipelineActivityState.Rejected,
                        $"Strategy rejected: {string.Join("; ", strategy.RejectionReasons)}",
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    _logger.LogInformation("Strategy rejected pattern {Pattern} for {Symbol}: {Reasons}", pattern.PatternType, pattern.Symbol, string.Join("; ", strategy.RejectionReasons));
                    await MarkBlockedAsync(pattern, "Strategy", strategy.RejectionReasons, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.StrategyAndRisk,
                    TradingPipelineActivityState.Active,
                    "Checking deterministic risk limits.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                var accountId = _ibkrSettings.AccountId ?? string.Empty;
                var account = await accountService.GetAccountInfoAsync(accountId, cancellationToken).ConfigureAwait(false);
                var positions = (await positionService.GetPositionsAsync(accountId, cancellationToken).ConfigureAwait(false)).ToList();
                var openOrders = (await orderExecutionService.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false)).ToList();
                var risk = await riskEngine.EvaluateAsync(strategy, account, positions, Array.Empty<Trade>(), openOrders, cancellationToken).ConfigureAwait(false);

                if (risk.Decision != RiskDecisionType.Approve)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.StrategyAndRisk,
                        TradingPipelineActivityState.Rejected,
                        $"Risk rejected: {risk.Reason}",
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    _logger.LogInformation("Risk rejected pattern {Pattern} for {Symbol}: {Reason}", pattern.PatternType, pattern.Symbol, risk.Reason);
                    await MarkBlockedAsync(pattern, "Risk", new[] { risk.Reason }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.OrderExecution,
                    TradingPipelineActivityState.Active,
                    "Approved by strategy and risk; handing to order workflow.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                await _pipelineChannel.PublishAsync(new ApprovedTradePlan
                {
                    Snapshot = snapshot,
                    Pattern = pattern,
                    AiAnalysis = analysis,
                    AiCriticAnalysis = critic,
                    StrategyDecision = strategy,
                    RiskDecision = risk
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Pattern processing canceled during shutdown for {Pattern} {Symbol}", pattern.PatternType, pattern.Symbol);
                throw;
            }
            catch (Exception ex)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Error,
                    "Pattern decision processing failed.",
                    pattern.Symbol,
                    pattern.PatternType.ToString());

                _logger.LogError(ex, "Failed to process pattern {Pattern} for {Symbol}", pattern.PatternType, pattern.Symbol);
                await MarkBlockedAsync(pattern, "PipelineError", new[] { ex.Message }, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task MarkBlockedAsync(
            PatternCandidate pattern,
            string stage,
            IReadOnlyCollection<string> reasons,
            CancellationToken cancellationToken)
        {
            if (_candidateResearchService == null) return;
            try
            {
                await _candidateResearchService.MarkBlockedAsync(pattern.PatternKey, stage, reasons, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mark research candidate {CandidateKey} as blocked at {Stage}.", pattern.PatternKey, stage);
            }
        }

        private bool ShouldCallCritic(AiMarketAnalysisResult analysis, out string reason)
        {
            if (analysis.IsSafeFallback)
            {
                reason = "Analyzer returned safe fallback; no trade.";
                return false;
            }

            if (!string.Equals(analysis.Action, AiMarketActions.Buy, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Analyzer action was {analysis.Action}; critic only runs for BUY.";
                return false;
            }

            if (analysis.Confidence < _tradingSettings.MinimumAiConfidence)
            {
                reason = $"Analyzer BUY confidence {analysis.Confidence:0.####} is below minimum {_tradingSettings.MinimumAiConfidence:0.####}.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private bool IsWithinTradingHours(DateTime timestampUtc)
        {
            var eastern = ResolveEasternTimeZone();
            var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(timestampUtc), eastern);
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;

            var start = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingStartHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingStartMinuteNewYork, 0, 59),
                0);
            var end = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingEndHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingEndMinuteNewYork, 0, 59),
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
            return timestamp.Kind switch
            {
                DateTimeKind.Utc => timestamp,
                DateTimeKind.Local => timestamp.ToUniversalTime(),
                _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)
            };
        }

        private static string FormatAge(TimeSpan age)
        {
            if (age < TimeSpan.Zero) return "0s";
            if (age.TotalSeconds < 60) return $"{Math.Floor(age.TotalSeconds)}s";
            if (age.TotalMinutes < 60) return $"{Math.Floor(age.TotalMinutes)}m";
            return $"{Math.Floor(age.TotalHours)}h";
        }

        private async Task PersistPipelineDecisionAsync(
            string recordType,
            PatternCandidate pattern,
            string reason,
            CancellationToken cancellationToken,
            AiMarketAnalysisResult? analysis = null,
            TradeSetupCandidate? setupCandidate = null)
        {
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    recordType,
                    symbol = pattern.Symbol,
                    pattern = pattern.PatternType.ToString(),
                    patternTimeframe = pattern.Timeframe.ToString(),
                    patternDetectedAtUtc = pattern.DetectedAtUtc,
                    deterministicPatternQuality = pattern.Confidence,
                    minimumPatternQualityForAiAnalysis = _tradingSettings.MinimumPatternQualityForAiAnalysis,
                    minimumAiConfidence = _tradingSettings.MinimumAiConfidence,
                    tradeSetupQuality = setupCandidate?.Quality.FinalQuality,
                    qualityBreakdown = setupCandidate?.Quality,
                    gateReasons = setupCandidate?.GateReasons,
                    reason,
                    analyzerAction = analysis?.Action,
                    analyzerConfidence = analysis?.Confidence,
                    analyzerSafeFallback = analysis?.IsSafeFallback
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                db.AiAnalysisRecords.Add(new TradingBot.Persistence.AiAnalysisRecord
                {
                    TimestampUtc = DateTime.UtcNow,
                    AgentType = AiAgentType.Pipeline.ToString(),
                    Context = recordType,
                    ResultJson = payload
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(ex, "Failed to persist AI pipeline decision {RecordType} for {Pattern} {Symbol}. Trading remains fail-closed for this candidate.", recordType, pattern.PatternType, pattern.Symbol);
            }
        }
    }
}
