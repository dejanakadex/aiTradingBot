using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Background
{
    public sealed class MarketDataSubscriptionHostedService : BackgroundService
    {
        private static readonly TimeSpan ConnectionPollInterval = TimeSpan.FromSeconds(1);

        private readonly IMarketDataService _marketDataService;
        private readonly MarketDataPipeline _marketDataPipeline;
        private readonly IIbkrConnectionService _connectionService;
        private readonly IHistoricalBackfillService _historicalBackfill;
        private readonly ICandleHistoryService _candleHistory;
        private readonly IMarketDataCollectionStatusService _collectionStatus;
        private readonly TradingSettings _tradingSettings;
        private readonly MarketDataCollectionSettings _collectionSettings;
        private readonly IClock _clock;
        private readonly ILogger<MarketDataSubscriptionHostedService> _logger;

        public MarketDataSubscriptionHostedService(
            IMarketDataService marketDataService,
            MarketDataPipeline marketDataPipeline,
            IIbkrConnectionService connectionService,
            IHistoricalBackfillService historicalBackfill,
            ICandleHistoryService candleHistory,
            IMarketDataCollectionStatusService collectionStatus,
            IOptions<TradingSettings> tradingSettings,
            IOptions<MarketDataCollectionSettings> collectionSettings,
            IClock clock,
            ILogger<MarketDataSubscriptionHostedService> logger)
        {
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
            _marketDataPipeline = marketDataPipeline ?? throw new ArgumentNullException(nameof(marketDataPipeline));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _historicalBackfill = historicalBackfill ?? throw new ArgumentNullException(nameof(historicalBackfill));
            _candleHistory = candleHistory ?? throw new ArgumentNullException(nameof(candleHistory));
            _collectionStatus = collectionStatus ?? throw new ArgumentNullException(nameof(collectionStatus));
            _tradingSettings = tradingSettings?.Value ?? throw new ArgumentNullException(nameof(tradingSettings));
            _collectionSettings = collectionSettings?.Value ?? throw new ArgumentNullException(nameof(collectionSettings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_collectionSettings.Enabled)
            {
                _logger.LogInformation("Market data collection is disabled by MarketDataCollection configuration.");
                return;
            }

            var streams = _tradingSettings.GetEnabledInstruments()
                .SelectMany(instrument => instrument.MarketDataTimeframes
                    .Select(timeframe => new CollectionStream(instrument, NormalizeTimeframe(timeframe))))
                .ToArray();
            foreach (var stream in streams)
            {
                _collectionStatus.EnsureStream(
                    stream.StreamKey,
                    stream.Instrument.InstrumentId,
                    stream.Instrument.Symbol,
                    stream.Timeframe,
                    checked((int)GetHeartbeatTimeout(stream.Timeframe).TotalSeconds),
                    _clock.UtcNow);
            }
            await RestoreCheckpointsAsync(streams, stoppingToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Market data collection service started with {StreamCount} independent streams; operating mode and AI/trading readiness do not gate collection.",
                streams.Length);
            try
            {
                await Task.WhenAll(streams.Select(stream => RunStreamAsync(stream, stoppingToken))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Market data collection service cancellation requested.");
            }
            finally
            {
                foreach (var stream in streams)
                {
                    _collectionStatus.ReportStatus(
                        stream.StreamKey,
                        MarketDataCollectionStatus.Stopped,
                        "Collection service stopped.",
                        _connectionService.Status == ConnectionStatus.Connected,
                        false,
                        _clock.UtcNow);
                }
                _logger.LogInformation("Market data collection service stopped.");
            }
        }

        private async Task RunStreamAsync(CollectionStream stream, CancellationToken cancellationToken)
        {
            var initialDelay = TimeSpan.FromSeconds(_collectionSettings.ReconnectInitialDelaySeconds);
            var reconnectDelay = initialDelay;
            var gapFillRequired = _collectionStatus.GetAll()
                .Single(item => item.StreamKey.Equals(stream.StreamKey, StringComparison.OrdinalIgnoreCase))
                .LastEventTimeUtc.HasValue;
            while (!cancellationToken.IsCancellationRequested)
            {
                await WaitForConnectionAsync(stream, cancellationToken).ConfigureAwait(false);
                if (gapFillRequired)
                {
                    try
                    {
                        await TryGapFillAsync(stream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _collectionStatus.ReportStatus(
                            stream.StreamKey,
                            MarketDataCollectionStatus.Reconnecting,
                            "Automatic gap fill raised an error; live resubscription will continue.",
                            true,
                            false,
                            _clock.UtcNow,
                            ex.Message);
                        _logger.LogWarning(ex, "Automatic gap-fill orchestration failed for {InstrumentId} {Timeframe}.", stream.Instrument.InstrumentId, stream.Timeframe);
                    }
                    gapFillRequired = false;
                }

                try
                {
                    _collectionStatus.ReportStatus(
                        stream.StreamKey,
                        MarketDataCollectionStatus.Subscribing,
                        "Creating broker market-data subscription.",
                        true,
                        false,
                        _clock.UtcNow);
                    await using var subscription = await _marketDataService
                        .SubscribeAsync(stream.Instrument.Symbol, stream.Timeframe, cancellationToken)
                        .ConfigureAwait(false);
                    var inSession = IsRegularSession(_clock.UtcNow);
                    _collectionStatus.ReportStatus(
                        stream.StreamKey,
                        inSession ? MarketDataCollectionStatus.Live : MarketDataCollectionStatus.IdleOutsideSession,
                        inSession ? "Subscription is live; waiting for heartbeat." : "Subscription is active outside the configured regular session.",
                        true,
                        true,
                        _clock.UtcNow);
                    _logger.LogInformation(
                        "Subscribed to market data for {InstrumentId} {Symbol} {Timeframe}.",
                        stream.Instrument.InstrumentId,
                        stream.Instrument.Symbol,
                        stream.Timeframe);

                    var ended = await ReadSubscriptionAsync(stream, subscription, cancellationToken).ConfigureAwait(false);
                    gapFillRequired = true;
                    if (ended.HadHeartbeat) reconnectDelay = initialDelay;
                    _collectionStatus.ReportReconnect(stream.StreamKey, ended.Reason, _clock.UtcNow);
                    _logger.LogWarning(
                        "Restarting only market-data stream {InstrumentId} {Timeframe}: {Reason}",
                        stream.Instrument.InstrumentId,
                        stream.Timeframe,
                        ended.Reason);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    gapFillRequired = true;
                    _collectionStatus.ReportStatus(
                        stream.StreamKey,
                        MarketDataCollectionStatus.Faulted,
                        "Subscription attempt failed; this stream will retry independently.",
                        _connectionService.Status == ConnectionStatus.Connected,
                        false,
                        _clock.UtcNow,
                        ex.Message);
                    _logger.LogWarning(
                        ex,
                        "Market-data stream failed for {InstrumentId} {Symbol} {Timeframe}; other streams continue.",
                        stream.Instrument.InstrumentId,
                        stream.Instrument.Symbol,
                        stream.Timeframe);
                }

                await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                reconnectDelay = TimeSpan.FromSeconds(Math.Min(
                    _collectionSettings.ReconnectMaximumDelaySeconds,
                    Math.Max(_collectionSettings.ReconnectInitialDelaySeconds, reconnectDelay.TotalSeconds * 2d)));
            }
        }

        private async Task RestoreCheckpointsAsync(IEnumerable<CollectionStream> streams, CancellationToken cancellationToken)
        {
            foreach (var stream in streams)
            {
                try
                {
                    var candles = await _candleHistory.GetLastNCandlesAsync(
                        stream.Instrument.Symbol,
                        ToTimeframe(stream.Timeframe),
                        1,
                        cancellationToken).ConfigureAwait(false);
                    var latest = candles.LastOrDefault();
                    if (latest == null) continue;
                    var heartbeat = ToUtc(latest.ReceivedTimeUtc);
                    var eventTime = ToUtc(latest.TimestampUtc);
                    _collectionStatus.SeedCheckpoint(
                        stream.StreamKey,
                        eventTime,
                        heartbeat,
                        Math.Max(0L, (long)(heartbeat - eventTime).TotalMilliseconds));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _collectionStatus.ReportStatus(
                        stream.StreamKey,
                        MarketDataCollectionStatus.Faulted,
                        "Failed to restore the collection checkpoint; live subscription will still start.",
                        _connectionService.Status == ConnectionStatus.Connected,
                        false,
                        _clock.UtcNow,
                        ex.Message);
                    _logger.LogWarning(ex, "Failed to restore collection checkpoint for {InstrumentId} {Timeframe}.", stream.Instrument.InstrumentId, stream.Timeframe);
                }
            }
        }

        private async Task WaitForConnectionAsync(CollectionStream stream, CancellationToken cancellationToken)
        {
            while (_connectionService.Status != ConnectionStatus.Connected)
            {
                _collectionStatus.ReportStatus(
                    stream.StreamKey,
                    MarketDataCollectionStatus.WaitingForConnection,
                    $"Waiting for IBKR connection; current status is {_connectionService.Status}.",
                    false,
                    false,
                    _clock.UtcNow);
                await Task.Delay(ConnectionPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<SubscriptionEnd> ReadSubscriptionAsync(
            CollectionStream stream,
            IMarketDataSubscription subscription,
            CancellationToken cancellationToken)
        {
            var timeout = GetHeartbeatTimeout(stream.Timeframe);
            var lastActivityUtc = ToUtc(_clock.UtcNow);
            var monitorInterval = TimeSpan.FromSeconds(_collectionSettings.MonitorIntervalSeconds);
            var hadHeartbeat = false;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (_connectionService.Status != ConnectionStatus.Connected)
                {
                    return new SubscriptionEnd($"IBKR changed to {_connectionService.Status}.", hadHeartbeat);
                }

                using var monitor = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                monitor.CancelAfter(monitorInterval);
                bool canRead;
                try
                {
                    canRead = await subscription.Reader.WaitToReadAsync(monitor.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    var now = ToUtc(_clock.UtcNow);
                    if (!IsRegularSession(now))
                    {
                        lastActivityUtc = now;
                        _collectionStatus.ReportStatus(
                            stream.StreamKey,
                            MarketDataCollectionStatus.IdleOutsideSession,
                            "Subscription is active outside the configured regular session.",
                            true,
                            true,
                            now);
                        continue;
                    }

                    if (now - lastActivityUtc > timeout)
                    {
                        _collectionStatus.ReportStatus(
                            stream.StreamKey,
                            MarketDataCollectionStatus.Stale,
                            $"No heartbeat for {(now - lastActivityUtc).TotalSeconds:F0}s; timeout is {timeout.TotalSeconds:F0}s.",
                            true,
                            true,
                            now,
                            "Heartbeat timeout.");
                        return new SubscriptionEnd("Heartbeat became stale.", hadHeartbeat);
                    }
                    continue;
                }

                if (!canRead) return new SubscriptionEnd("Broker subscription channel completed.", hadHeartbeat);
                while (subscription.Reader.TryRead(out var bar))
                {
                    var receivedAt = ToUtc(_clock.UtcNow);
                    await _marketDataPipeline.ProcessMarketBarAsync(
                        bar,
                        cancellationToken,
                        instrumentId: stream.Instrument.InstrumentId,
                        source: "IBKR.StreamingBar").ConfigureAwait(false);
                    lastActivityUtc = receivedAt;
                    hadHeartbeat = true;
                    var eventTime = ToUtc(bar.TimestampUtc);
                    var lag = Math.Max(0L, (long)(receivedAt - eventTime).TotalMilliseconds);
                    _collectionStatus.ReportHeartbeat(stream.StreamKey, eventTime, receivedAt, lag);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new SubscriptionEnd("Collection canceled.", hadHeartbeat);
        }

        private async Task TryGapFillAsync(CollectionStream stream, CancellationToken cancellationToken)
        {
            if (!_collectionSettings.AutoGapFillEnabled || !IsRegularSession(_clock.UtcNow)) return;
            var snapshot = _collectionStatus.GetAll()
                .Single(item => item.StreamKey.Equals(stream.StreamKey, StringComparison.OrdinalIgnoreCase));
            if (!snapshot.LastEventTimeUtc.HasValue) return;

            var interval = GetInterval(stream.Timeframe);
            var end = AlignDown(ToUtc(_clock.UtcNow), interval);
            var start = ToUtc(snapshot.LastEventTimeUtc.Value).Add(interval);
            var earliest = end.AddMinutes(-_collectionSettings.MaximumGapFillLookbackMinutes);
            if (start < earliest) start = AlignDown(earliest, interval);
            if (start >= end) return;

            var result = await _historicalBackfill.FillGapAsync(
                stream.Instrument.InstrumentId,
                stream.Instrument.Symbol,
                stream.Timeframe,
                start,
                end,
                cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _collectionStatus.ReportGapFill(stream.StreamKey, start, end, result.BarsInserted, _clock.UtcNow);
            }
            else
            {
                _collectionStatus.ReportStatus(
                    stream.StreamKey,
                    MarketDataCollectionStatus.Reconnecting,
                    "Automatic gap fill failed; live resubscription will continue.",
                    true,
                    false,
                    _clock.UtcNow,
                    result.Error);
            }
        }

        private TimeSpan GetHeartbeatTimeout(string timeframe)
        {
            var interval = GetInterval(timeframe);
            return TimeSpan.FromTicks(checked((long)(interval.Ticks * _collectionSettings.HeartbeatIntervalMultiplier)))
                .Add(TimeSpan.FromSeconds(_collectionSettings.HeartbeatGraceSeconds));
        }

        private bool IsRegularSession(DateTime utcNow)
        {
            if (!_collectionSettings.RegularSessionOnly) return true;
            var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(utcNow), ResolveEasternTimeZone());
            if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
            var start = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingStartHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingStartMinuteNewYork, 0, 59),
                0);
            var end = new TimeSpan(
                Math.Clamp(_tradingSettings.TradingEndHourNewYork, 0, 23),
                Math.Clamp(_tradingSettings.TradingEndMinuteNewYork, 0, 59),
                0);
            return local.TimeOfDay >= start && local.TimeOfDay < end;
        }

        private static string NormalizeTimeframe(string timeframe) => timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" or "1 min" or "1 minute" => "1m",
            "5m" or "5 mins" or "5 minutes" => "5m",
            "15m" or "15 mins" or "15 minutes" => "15m",
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported collection timeframe.")
        };

        private static TimeSpan GetInterval(string timeframe) => NormalizeTimeframe(timeframe) switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        private static Timeframe ToTimeframe(string timeframe) => NormalizeTimeframe(timeframe) switch
        {
            "1m" => Timeframe.OneMinute,
            "5m" => Timeframe.FiveMinutes,
            "15m" => Timeframe.FifteenMinutes,
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        private static DateTime AlignDown(DateTime value, TimeSpan interval)
        {
            var utc = ToUtc(value);
            return new DateTime(utc.Ticks - utc.Ticks % interval.Ticks, DateTimeKind.Utc);
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        }

        private sealed record CollectionStream(ConfiguredInstrument Instrument, string Timeframe)
        {
            public string StreamKey => $"{Instrument.InstrumentId}|BAR|{Timeframe}".ToUpperInvariant();
        }

        private sealed record SubscriptionEnd(string Reason, bool HadHeartbeat);
    }
}
