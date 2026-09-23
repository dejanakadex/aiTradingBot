using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public class MarketDataPipeline : IDisposable
    {
        private readonly IIbkrAdapter _adapter;
        private readonly ITradingEventBus _eventBus;
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IMarketDataValidator _validator;
        private readonly IExitManagementService? _exitManagementService;
        private readonly ITradingPipelineStatusService? _pipelineStatusService;
        private readonly IMarketDataQualityService _qualityService;
        private readonly ILatestMarketDataService _latestMarketData;
        private readonly TradingSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<MarketDataPipeline> _logger;

        public MarketDataPipeline(
            IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            ILogger<MarketDataPipeline> logger)
            : this(
                adapter,
                eventBus,
                dbFactory,
                new MarketDataValidator(Microsoft.Extensions.Options.Options.Create(new TradingSettings()), new SystemClock()),
                null,
                logger)
        {
        }

        public MarketDataPipeline(
            IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IMarketDataValidator validator,
            ILogger<MarketDataPipeline> logger)
            : this(adapter, eventBus, dbFactory, validator, null, logger)
        {
        }

        public MarketDataPipeline(
            IIbkrAdapter adapter,
            ITradingEventBus eventBus,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IMarketDataValidator validator,
            IExitManagementService? exitManagementService,
            ILogger<MarketDataPipeline> logger,
            ITradingPipelineStatusService? pipelineStatusService = null,
            IMarketDataQualityService? qualityService = null,
            ILatestMarketDataService? latestMarketData = null,
            IOptions<TradingSettings>? settings = null,
            IClock? clock = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _exitManagementService = exitManagementService;
            _pipelineStatusService = pipelineStatusService;
            _settings = settings?.Value ?? new TradingSettings();
            _clock = clock ?? new SystemClock();
            _qualityService = qualityService ?? new MarketDataQualityService(
                dbFactory,
                Microsoft.Extensions.Options.Options.Create(_settings),
                _clock,
                NullLogger<MarketDataQualityService>.Instance);
            _latestMarketData = latestMarketData ?? new LatestMarketDataService();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _adapter.MarketBarReceived += OnMarketBarReceived;
            _adapter.MarketDataEventReceived += OnMarketDataEventReceived;
        }

        private Task OnMarketBarReceived(MarketBar bar) => ProcessMarketBarAsync(bar);
        private async Task OnMarketDataEventReceived(CanonicalMarketDataEvent marketEvent) =>
            await ProcessCanonicalEventAsync(marketEvent).ConfigureAwait(false);

        public async Task ProcessMarketBarAsync(
            MarketBar bar,
            CancellationToken cancellationToken = default,
            bool allowStaleSeedCandle = false,
            bool publishToEventBus = true,
            string? instrumentId = null,
            string? source = null)
        {
            if (bar == null) throw new ArgumentNullException(nameof(bar));
            var resolvedInstrumentId = ResolveInstrumentId(instrumentId, bar.InstrumentId, bar.Symbol);
            var canonical = new CanonicalMarketDataEvent
            {
                EventId = BuildBarEventId(resolvedInstrumentId, bar),
                InstrumentId = resolvedInstrumentId,
                Symbol = bar.Symbol.Trim().ToUpperInvariant(),
                Kind = MarketDataEventKind.Bar,
                EventTimeUtc = ToUtc(bar.TimestampUtc),
                ReceivedTimeUtc = ToUtc(bar.ReceivedTimeUtc ?? _clock.UtcNow),
                Source = NormalizeSource(source, bar.Source, publishToEventBus ? "IBKR.StreamingBar" : "IBKR.HistoricalBar"),
                Sequence = bar.Sequence,
                IsFinal = bar.IsFinal,
                Timeframe = NormalizeTimeframe(bar.Timeframe),
                Open = bar.Open,
                High = bar.High,
                Low = bar.Low,
                Close = bar.Close,
                Volume = bar.Volume
            };

            try
            {
                if (publishToEventBus)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.ReceivingCandle,
                        TradingPipelineActivityState.Active,
                        $"Received {canonical.InstrumentId} {canonical.Timeframe} candle.",
                        canonical.Symbol);
                }

                var assessment = await _qualityService
                    .EvaluateAsync(canonical, allowStaleSeedCandle, cancellationToken)
                    .ConfigureAwait(false);
                if (!assessment.CanPersist)
                {
                    MarkRejected(canonical, assessment, publishToEventBus);
                    return;
                }

                if (!_validator.TryValidate(bar, out var validationReason)
                    && (!allowStaleSeedCandle || !validationReason.Equals("Candle timestamp is stale.", StringComparison.OrdinalIgnoreCase)))
                {
                    MarkRejected(canonical, new MarketDataQualityAssessment(MarketDataQualityStatus.Invalid, validationReason, false, false), publishToEventBus);
                    return;
                }

                if (!TryParseTimeframe(canonical.Timeframe, out var timeframe))
                {
                    MarkRejected(canonical, new MarketDataQualityAssessment(MarketDataQualityStatus.Invalid, "Unsupported timeframe.", false, false), publishToEventBus);
                    return;
                }

                var candle = new TradingBot.Domain.Models.Candle(
                    canonical.Symbol,
                    timeframe,
                    canonical.EventTimeUtc,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume,
                    canonical.InstrumentId,
                    canonical.ReceivedTimeUtc,
                    canonical.Source,
                    canonical.IsFinal,
                    assessment.Status);

                var persisted = await PersistCandleAsync(canonical, timeframe, assessment.Status, cancellationToken).ConfigureAwait(false);
                if (!persisted)
                {
                    if (publishToEventBus)
                    {
                        _pipelineStatusService?.Mark(
                            TradingPipelineStage.ReceivingCandle,
                            TradingPipelineActivityState.Error,
                            "Candle was duplicate or could not be persisted; analysis skipped.",
                            canonical.Symbol);
                    }
                    return;
                }

                if (!assessment.IsHealthy)
                {
                    MarkRejected(canonical, assessment, publishToEventBus);
                    return;
                }

                _latestMarketData.Apply(canonical);
                if (_exitManagementService != null && publishToEventBus)
                {
                    await _exitManagementService.ProcessMarketCandleAsync(candle, cancellationToken).ConfigureAwait(false);
                }

                if (publishToEventBus && !_eventBus.TryPublish(candle))
                {
                    _logger.LogWarning("Failed to publish candle for {InstrumentId} {Timeframe} {Timestamp}", canonical.InstrumentId, canonical.Timeframe, canonical.EventTimeUtc);
                }
                else if (!publishToEventBus)
                {
                    _logger.LogDebug(
                        "Persisted historical candle without publishing trade pipeline event for {InstrumentId} {Timeframe} {Timestamp}",
                        canonical.InstrumentId,
                        canonical.Timeframe,
                        canonical.EventTimeUtc);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (publishToEventBus)
                {
                    _pipelineStatusService?.Mark(
                        TradingPipelineStage.ReceivingCandle,
                        TradingPipelineActivityState.Error,
                        "Market data processing failed.",
                        canonical.Symbol);
                }
                _logger.LogError(ex, "Error processing canonical market bar {EventId}", canonical.EventId);
            }
        }

        public async Task<MarketDataQualityAssessment> ProcessCanonicalEventAsync(
            CanonicalMarketDataEvent marketEvent,
            CancellationToken cancellationToken = default)
        {
            if (marketEvent == null) throw new ArgumentNullException(nameof(marketEvent));
            if (marketEvent.Kind == MarketDataEventKind.Bar)
            {
                throw new ArgumentException("Use ProcessMarketBarAsync for bar events so final candles are persisted before publication.", nameof(marketEvent));
            }

            var normalized = marketEvent with
            {
                InstrumentId = ResolveInstrumentId(null, marketEvent.InstrumentId, marketEvent.Symbol),
                EventTimeUtc = ToUtc(marketEvent.EventTimeUtc),
                ReceivedTimeUtc = ToUtc(marketEvent.ReceivedTimeUtc),
                Symbol = marketEvent.Symbol.Trim().ToUpperInvariant(),
                Source = marketEvent.Source.Trim()
            };
            var assessment = await _qualityService.EvaluateAsync(normalized, false, cancellationToken).ConfigureAwait(false);
            if (assessment.IsHealthy)
            {
                _latestMarketData.Apply(normalized);
            }
            return assessment;
        }

        private async Task<bool> PersistCandleAsync(
            CanonicalMarketDataEvent marketEvent,
            Timeframe timeframe,
            MarketDataQualityStatus qualityStatus,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var ctx = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var exists = await ctx.Candles.AnyAsync(
                    candle => candle.Symbol == marketEvent.Symbol
                        && candle.Timeframe == timeframe
                        && candle.TimestampUtc == marketEvent.EventTimeUtc,
                    cancellationToken).ConfigureAwait(false);
                if (exists)
                {
                    _logger.LogInformation("Duplicate candle detected for {InstrumentId} {Timeframe} {Timestamp}", marketEvent.InstrumentId, marketEvent.Timeframe, marketEvent.EventTimeUtc);
                    return false;
                }

                ctx.Candles.Add(new TradingBot.Persistence.Candle
                {
                    InstrumentId = marketEvent.InstrumentId,
                    Symbol = marketEvent.Symbol,
                    Timeframe = timeframe,
                    TimestampUtc = marketEvent.EventTimeUtc,
                    ReceivedTimeUtc = marketEvent.ReceivedTimeUtc,
                    Source = marketEvent.Source,
                    IsFinal = marketEvent.IsFinal,
                    QualityStatus = qualityStatus,
                    Open = marketEvent.Open!.Value,
                    High = marketEvent.High!.Value,
                    Low = marketEvent.Low!.Value,
                    Close = marketEvent.Close!.Value,
                    Volume = marketEvent.Volume!.Value
                });
                await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "Failed to persist candle for {InstrumentId} {Timeframe} {Timestamp}", marketEvent.InstrumentId, marketEvent.Timeframe, marketEvent.EventTimeUtc);
                return false;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Failed to persist candle for {InstrumentId} {Timeframe} {Timestamp}", marketEvent.InstrumentId, marketEvent.Timeframe, marketEvent.EventTimeUtc);
                return false;
            }
        }

        private void MarkRejected(CanonicalMarketDataEvent marketEvent, MarketDataQualityAssessment assessment, bool publishToEventBus)
        {
            if (publishToEventBus)
            {
                _pipelineStatusService?.Mark(
                    TradingPipelineStage.ReceivingCandle,
                    TradingPipelineActivityState.Error,
                    $"Market data blocked: {assessment.Status} — {assessment.Reason}",
                    marketEvent.Symbol);
            }
            _logger.LogWarning(
                "Blocked canonical market data {EventId} for {InstrumentId}: {Status} {Reason}",
                marketEvent.EventId,
                marketEvent.InstrumentId,
                assessment.Status,
                assessment.Reason);
        }

        private string ResolveInstrumentId(string? requested, string? fromBar, string symbol)
        {
            var supplied = string.IsNullOrWhiteSpace(requested) ? fromBar : requested;
            var configured = _settings.GetConfiguredInstruments();
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                var normalized = supplied.Trim();
                var byId = configured.SingleOrDefault(item => item.InstrumentId.Equals(normalized, StringComparison.OrdinalIgnoreCase));
                if (byId != null) return byId.InstrumentId;
                if (!normalized.Equals(symbol.Trim(), StringComparison.OrdinalIgnoreCase)) return normalized;
            }

            return configured.SingleOrDefault(item => item.Symbol.Equals(symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                ?.InstrumentId ?? symbol.Trim().ToUpperInvariant();
        }

        private static string BuildBarEventId(string instrumentId, MarketBar bar)
        {
            var canonical = string.Join('|',
                instrumentId.Trim().ToUpperInvariant(),
                MarketDataEventKind.Bar,
                NormalizeTimeframe(bar.Timeframe),
                ToUtc(bar.TimestampUtc).ToString("O"),
                bar.Sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                bar.Open.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bar.High.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bar.Low.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bar.Close.ToString(System.Globalization.CultureInfo.InvariantCulture),
                bar.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static bool TryParseTimeframe(string timeframe, out Timeframe parsed)
        {
            parsed = timeframe switch
            {
                "1m" => Timeframe.OneMinute,
                "5m" => Timeframe.FiveMinutes,
                "15m" => Timeframe.FifteenMinutes,
                _ => default
            };
            return timeframe is "1m" or "5m" or "15m";
        }

        private static string NormalizeTimeframe(string timeframe) => timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" or "1 min" or "1 minute" => "1m",
            "5m" or "5 mins" or "5 minutes" => "5m",
            "15m" or "15 mins" or "15 minutes" => "15m",
            _ => timeframe.Trim().ToLowerInvariant()
        };

        private static string NormalizeSource(string? requested, string? fromBar, string fallback)
        {
            var value = string.IsNullOrWhiteSpace(requested) ? fromBar : requested;
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        public void Dispose()
        {
            try
            {
                _adapter.MarketBarReceived -= OnMarketBarReceived;
                _adapter.MarketDataEventReceived -= OnMarketDataEventReceived;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to detach market bar event handler during disposal.");
            }
        }
    }
}
