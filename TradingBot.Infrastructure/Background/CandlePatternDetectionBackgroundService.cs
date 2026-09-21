using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        private readonly ConcurrentDictionary<string, byte> _processedCandles = new();

        public CandlePatternDetectionBackgroundService(
            ITradingEventBus eventBus,
            ICandleHistoryService candleHistoryService,
            IFeatureEngine featureEngine,
            IPatternDetector patternDetector,
            ITradingEngineStatusService statusService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<CandlePatternDetectionBackgroundService> logger,
            ITradingPipelineStatusService? pipelineStatusService = null)
        {
            _eventBus = eventBus;
            _candleHistoryService = candleHistoryService;
            _featureEngine = featureEngine;
            _patternDetector = patternDetector;
            _statusService = statusService;
            _pipelineStatusService = pipelineStatusService;
            _dbFactory = dbFactory;
            _logger = logger;
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
            var key = $"{candle.Symbol}:{candle.Timeframe}:{candle.TimestampUtc:O}";
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

                _pipelineStatusService?.Mark(
                    TradingPipelineStage.PatternAnalysis,
                    TradingPipelineActivityState.Active,
                    $"Analyzing {candles.Count} candles for patterns.",
                    candle.Symbol);

                var features = _featureEngine.ComputeFeatures(candles);
                var patterns = _patternDetector.Detect(candles);

                _logger.LogInformation(
                    "Processed candle {Symbol} {Timeframe} {TimestampUtc}; trend={Trend}, patterns={PatternCount}",
                    candle.Symbol,
                    candle.Timeframe,
                    candle.TimestampUtc,
                    features.TrendDirection,
                    patterns.Count);

                if (patterns.Count == 0)
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
                relevantPriceLevels = pattern.RelevantPriceLevels,
                metadata = pattern.Metadata,
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
                    p => p.Symbol == pattern.Symbol
                        && p.PatternType == pattern.PatternType
                        && p.DetectedAtUtc == pattern.DetectedAtUtc,
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
                    Symbol = pattern.Symbol,
                    PatternType = pattern.PatternType,
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
