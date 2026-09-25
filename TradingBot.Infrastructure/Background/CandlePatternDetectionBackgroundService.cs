using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Background
{
    public sealed class CandlePatternDetectionBackgroundService : BackgroundService
    {
        private const int HistoryCandles = 80;

        private readonly ITradingEventBus _eventBus;
        private readonly ICandleHistoryService _candleHistoryService;
        private readonly IFeatureEngine _featureEngine;
        private readonly IPatternDetector _patternDetector;
        private readonly ITradingEngineStatusService _statusService;
        private readonly ITradingPipelineStatusService? _pipelineStatusService;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ILogger<CandlePatternDetectionBackgroundService> _logger;
        private readonly TradingSettings _tradingSettings;
        private readonly ILatestMarketDataService? _latestMarketDataService;
        private readonly ICandidateResearchService? _candidateResearchService;
        private readonly ConcurrentDictionary<string, byte> _processedCandles = new();

        public CandlePatternDetectionBackgroundService(
            ITradingEventBus eventBus,
            ICandleHistoryService candleHistoryService,
            IFeatureEngine featureEngine,
            IPatternDetector patternDetector,
            ITradingEngineStatusService statusService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<CandlePatternDetectionBackgroundService> logger,
            ITradingPipelineStatusService? pipelineStatusService = null,
            IOptions<TradingSettings>? tradingSettings = null,
            ILatestMarketDataService? latestMarketDataService = null,
            ICandidateResearchService? candidateResearchService = null)
        {
            _eventBus = eventBus;
            _candleHistoryService = candleHistoryService;
            _featureEngine = featureEngine;
            _patternDetector = patternDetector;
            _statusService = statusService;
            _pipelineStatusService = pipelineStatusService;
            _dbFactory = dbFactory;
            _logger = logger;
            _tradingSettings = tradingSettings?.Value ?? new TradingSettings();
            _latestMarketDataService = latestMarketDataService;
            _candidateResearchService = candidateResearchService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Candle pattern detection service started");
            try
            {
                await foreach (var candle in _eventBus.CandleReader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                {
                    await ProcessCandleAsync(candle, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Candle pattern detection service cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical failure in candle pattern detection service");
                _statusService.SetState(TradingEngineState.Faulted, false, "Candle pattern detection service failed.", new[] { ex.Message });
            }
            finally
            {
                _logger.LogInformation("Candle pattern detection service stopped");
            }
        }

        private async Task ProcessCandleAsync(Candle candle, CancellationToken cancellationToken)
        {
            if (!candle.IsFinal || candle.QualityStatus != MarketDataQualityStatus.Healthy)
            {
                _logger.LogWarning(
                    "Skipped non-tradable candle {InstrumentId} {Symbol} {Timeframe} {TimestampUtc}: final={IsFinal}, quality={QualityStatus}",
                    candle.InstrumentId,
                    candle.Symbol,
                    candle.Timeframe,
                    candle.TimestampUtc,
                    candle.IsFinal,
                    candle.QualityStatus);
                return;
            }

            var key = $"{candle.InstrumentId}:{candle.Timeframe}:{candle.TimestampUtc:O}";
            if (!_processedCandles.TryAdd(key, 0))
            {
                _logger.LogDebug("Skipping duplicate candle {Key}", key);
                return;
            }

            try
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.FetchingMarketContext,
                    TradingPipelineActivityState.Active,
                    $"Fetching recent candles for {candle.Symbol} {candle.Timeframe}.",
                    candle.Symbol);

                var candles = await _candleHistoryService.GetLastNCandlesAsync(candle.Symbol, candle.Timeframe, HistoryCandles, cancellationToken).ConfigureAwait(false);
                if (candles.Count == 0 || candles[^1].TimestampUtc != candle.TimestampUtc)
                {
                    candles = candles.Concat(new[] { candle }).OrderBy(c => c.TimestampUtc).ToList();
                }
                candles = candles
                    .Where(item => item.TimestampUtc <= candle.TimestampUtc)
                    .GroupBy(item => item.TimestampUtc)
                    .Select(group => group.OrderBy(item => item.ReceivedTimeUtc).ThenBy(item => item.Source, StringComparer.Ordinal).Last())
                    .OrderBy(item => item.TimestampUtc)
                    .TakeLast(HistoryCandles)
                    .ToArray();

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Active,
                    $"Analyzing {candles.Count} candles for patterns.",
                    candle.Symbol);

                var latest = _latestMarketDataService?.Get(candle.InstrumentId)
                    ?? _latestMarketDataService?.Get(candle.Symbol);
                var features = _featureEngine.ComputeFeatures(new CanonicalFeatureInput
                {
                    Candles = candles,
                    AsOfUtc = candle.TimestampUtc,
                    Bid = latest?.Bid,
                    BidTimeUtc = latest?.BidTimeUtc,
                    Ask = latest?.Ask,
                    AskTimeUtc = latest?.AskTimeUtc,
                    LastTrade = latest?.LastTrade,
                    LastTradeTimeUtc = latest?.LastTradeTimeUtc
                });
                var configuredInstrument = _tradingSettings.GetEnabledInstruments()
                    .FirstOrDefault(configured => string.Equals(configured.InstrumentId, candle.InstrumentId, StringComparison.OrdinalIgnoreCase))
                    ?? _tradingSettings.GetEnabledInstruments()
                        .FirstOrDefault(configured => string.Equals(configured.Symbol, candle.Symbol, StringComparison.OrdinalIgnoreCase));
                var strategyIds = configuredInstrument?.StrategyIds.Count > 0
                    ? configuredInstrument.StrategyIds
                    : new[] { PipelineContractVersions.DefaultStrategyId };
                var directions = configuredInstrument?.AllowedDirections.Count > 0
                    ? configuredInstrument.AllowedDirections
                    : new[] { TradeDirection.Long };
                var detectedPatterns = new List<PatternCandidate>();
                foreach (var strategyId in strategyIds)
                {
                    var batch = _patternDetector.Process(new PatternDetectionInput
                    {
                        Candles = candles,
                        StrategyId = strategyId,
                        FeatureVersion = _featureEngine.FeatureVersion,
                        Directions = directions
                    });
                    if (_candidateResearchService != null)
                    {
                        var sourceEventId = $"LIVE-BAR|{candle.InstrumentId}|{candle.Timeframe}|{candle.TimestampUtc:O}".ToUpperInvariant();
                        var evaluationContext = new CandidateEvaluationContext(features.Regime, features.NormalizedLiquidity, features.AtrToPriceRatio);
                        await _candidateResearchService.PersistBatchAsync(batch, sourceEventId, evaluationContext: evaluationContext, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    detectedPatterns.AddRange(batch.Candidates);
                }
                var patterns = detectedPatterns
                    .DistinctBy(pattern => pattern.PatternKey, StringComparer.Ordinal)
                    .ToArray();

                _logger.LogInformation(
                    "Processed candle {Symbol} {Timeframe} {TimestampUtc}; trend={Trend}, patterns={PatternCount}",
                    candle.Symbol,
                    candle.Timeframe,
                    candle.TimestampUtc,
                    features.TrendDirection,
                    patterns.Length);

                if (patterns.Length == 0)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.PatternAnalysis,
                        TradingPipelineActivityState.Completed,
                        $"No trade pattern on {candle.Symbol} {candle.Timeframe}.",
                        candle.Symbol);

                    _logger.LogDebug("No deterministic pattern detected for {Symbol} {Timeframe} {TimestampUtc}", candle.Symbol, candle.Timeframe, candle.TimestampUtc);
                }

                foreach (var pattern in patterns)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.PatternAnalysis,
                        TradingPipelineActivityState.Active,
                        $"Detected {pattern.PatternType}; checking trade quality.",
                        pattern.Symbol,
                        pattern.PatternType.ToString());

                    await PersistPatternDetectionAsync(pattern, features, candles[^1], cancellationToken).ConfigureAwait(false);
                    await _eventBus.PublishAsync(pattern, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Candle processing canceled during shutdown for {Symbol} {Timeframe} {TimestampUtc}", candle.Symbol, candle.Timeframe, candle.TimestampUtc);
                throw;
            }
            catch (Exception ex)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Error,
                    "Pattern analysis failed.",
                    candle.Symbol);

                _logger.LogError(ex, "Failed to process candle {Symbol} {Timeframe} {TimestampUtc}", candle.Symbol, candle.Timeframe, candle.TimestampUtc);
            }
        }

        private async Task PersistPatternDetectionAsync(
            PatternCandidate pattern,
            MarketFeatures features,
            Candle latestCandle,
            CancellationToken cancellationToken)
        {
            var details = JsonSerializer.Serialize(new
            {
                confidence = pattern.Confidence,
                timeframe = pattern.Timeframe.ToString(),
                detectedAtUtc = pattern.DetectedAtUtc,
                context = pattern.Context,
                relevantPriceLevels = pattern.RelevantPriceLevels,
                metadata = pattern.Metadata,
                direction = pattern.Direction.ToString(),
                patternVersion = pattern.PatternVersion,
                hardConditions = pattern.HardConditions,
                scoreComponents = pattern.ScoreComponents,
                evaluationReasons = pattern.EvaluationReasons,
                featureVersion = features.FeatureVersion,
                canonicalFeatures = features,
                aboveVwap = features.Vwap.HasValue ? latestCandle.Close >= features.Vwap.Value : (bool?)null,
                vwap = features.Vwap,
                trendDirection = features.TrendDirection,
                volumeRatio = features.VolumeRatio,
                volatility = features.Volatility
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var exists = await db.PatternDetections.AnyAsync(
                    p => p.PatternKey == pattern.PatternKey,
                    cancellationToken).ConfigureAwait(false);

                if (exists)
                {
                    _logger.LogDebug(
                        "Duplicate pattern detection skipped for {Pattern} {Symbol} {DetectedAtUtc}.",
                        pattern.PatternType,
                        pattern.Symbol,
                        pattern.DetectedAtUtc);
                    return;
                }

                db.PatternDetections.Add(new TradingBot.Persistence.PatternDetection
                {
                    PatternKey = pattern.PatternKey,
                    SignalId = pattern.Context.SignalId,
                    InstrumentId = pattern.InstrumentId,
                    Symbol = pattern.Symbol,
                    StrategyId = pattern.StrategyId,
                    PatternType = pattern.PatternType,
                    Timeframe = pattern.Timeframe,
                    Direction = pattern.Direction,
                    PatternVersion = pattern.PatternVersion,
                    DetectedAtUtc = pattern.DetectedAtUtc,
                    Details = details
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(
                    ex,
                    "Failed to persist pattern detection for {Pattern} {Symbol}. Dashboard statistics may be incomplete.",
                    pattern.PatternType,
                    pattern.Symbol);
            }
        }
    }
}
