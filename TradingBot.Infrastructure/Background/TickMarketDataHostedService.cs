using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Background;

public sealed class TickMarketDataHostedService : BackgroundService
{
    private readonly ITickMarketDataService? _ticks;
    private readonly IIbkrConnectionService _connection;
    private readonly MarketDataPipeline _pipeline;
    private readonly IMarketDataCollectionStatusService _status;
    private readonly IMarketDataQualityService _quality;
    private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
    private readonly TradingSettings _trading;
    private readonly MarketDataCollectionSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<TickMarketDataHostedService> _logger;

    public TickMarketDataHostedService(
        IEnumerable<ITickMarketDataService> tickServices,
        IIbkrConnectionService connection,
        MarketDataPipeline pipeline,
        IMarketDataCollectionStatusService status,
        IMarketDataQualityService quality,
        IDbContextFactory<TradingBotDbContext> dbFactory,
        IOptions<TradingSettings> trading,
        IOptions<MarketDataCollectionSettings> settings,
        IClock clock,
        ILogger<TickMarketDataHostedService> logger)
    {
        _ticks = tickServices.SingleOrDefault();
        _connection = connection;
        _pipeline = pipeline;
        _status = status;
        _quality = quality;
        _dbFactory = dbFactory;
        _trading = trading.Value;
        _settings = settings.Value;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled || _ticks == null) return;
        var instruments = _trading.GetEnabledInstruments();
        var checkpoints = (await _quality.GetStreamsAsync(stoppingToken).ConfigureAwait(false))
            .Where(x => x.Kind is MarketDataEventKind.Bid or MarketDataEventKind.Ask or MarketDataEventKind.Trade)
            .Where(x => x.Source == "IBKR.TickByTick" && x.LastReceivedTimeUtc.HasValue)
            .GroupBy(x => x.InstrumentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Max(y => y.LastReceivedTimeUtc)!.Value, StringComparer.OrdinalIgnoreCase);

        foreach (var instrument in instruments)
        {
            _status.EnsureStream(Key(instrument.InstrumentId), instrument.InstrumentId, instrument.Symbol,
                "TICK", _settings.TickHeartbeatTimeoutSeconds, _clock.UtcNow);
        }

        try
        {
            await Task.WhenAll(instruments.Select(instrument => RunAsync(
                instrument,
                checkpoints.GetValueOrDefault(instrument.InstrumentId), stoppingToken))).ConfigureAwait(false);
        }
        finally
        {
            foreach (var instrument in instruments)
            {
                _status.ReportStatus(Key(instrument.InstrumentId), MarketDataCollectionStatus.Stopped,
                    "Tick collection stopped.", _connection.Status == ConnectionStatus.Connected, false, _clock.UtcNow);
            }
        }
    }

    private async Task RunAsync(ConfiguredInstrument instrument, DateTime checkpointUtc, CancellationToken token)
    {
        var key = Key(instrument.InstrumentId);
        var delay = _settings.ReconnectInitialDelaySeconds;
        var gapStart = checkpointUtc == default ? (DateTime?)null : checkpointUtc;
        var dropped = 0L;
        while (!token.IsCancellationRequested)
        {
            try
            {
                while (_connection.Status != ConnectionStatus.Connected)
                {
                    _status.ReportStatus(key, MarketDataCollectionStatus.WaitingForConnection,
                        "Waiting for IBKR connection.", false, false, _clock.UtcNow);
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }

                _status.ReportStatus(key, MarketDataCollectionStatus.Subscribing,
                    "Requesting IBKR AllLast and BidAsk tick-by-tick streams.", true, false, _clock.UtcNow);
                await using var subscription = await _ticks!.SubscribeTicksAsync(
                    instrument.InstrumentId, instrument.Symbol, token).ConfigureAwait(false);
                var subscribedAt = _clock.UtcNow;
                if (gapStart.HasValue && gapStart.Value < subscribedAt)
                {
                    await RecordGapAsync(instrument, gapStart.Value, subscribedAt,
                        "Tick coverage unknown during process or broker interruption; minute bars do not repair ticks.", 0, token).ConfigureAwait(false);
                }
                gapStart = subscribedAt;
                _status.ReportStatus(key, MarketDataCollectionStatus.Live,
                    "Tick requests active; waiting for broker callbacks.", true, true, subscribedAt);
                delay = _settings.ReconnectInitialDelaySeconds;
                var lastActivity = subscribedAt;
                var lastDropped = 0L;

                while (!token.IsCancellationRequested)
                {
                    if (_connection.Status != ConnectionStatus.Connected)
                        throw new IOException("IBKR connection lost.");

                    using var monitor = CancellationTokenSource.CreateLinkedTokenSource(token);
                    monitor.CancelAfter(TimeSpan.FromSeconds(_settings.MonitorIntervalSeconds));
                    bool available;
                    try { available = await subscription.Reader.WaitToReadAsync(monitor.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        if (subscription.DroppedEvents > lastDropped)
                        {
                            dropped = subscription.DroppedEvents - lastDropped;
                            throw new IOException($"Tick queue overflow: {subscription.DroppedEvents - lastDropped} events dropped.");
                        }
                        var now = _clock.UtcNow;
                        if (IsRegularSession(now) && now - lastActivity > TimeSpan.FromSeconds(_settings.TickHeartbeatTimeoutSeconds))
                            throw new IOException("Tick heartbeat timeout.");
                        if (!IsRegularSession(now)) lastActivity = now;
                        continue;
                    }
                    if (!available) throw new IOException("Tick channel completed.");
                    while (subscription.Reader.TryRead(out var marketEvent))
                    {
                        var assessment = await _pipeline.ProcessCanonicalEventAsync(marketEvent, token).ConfigureAwait(false);
                        if (!assessment.CanPersist)
                            throw new IOException($"Tick quality rejected: {assessment.Status} {assessment.Reason}");
                        lastActivity = _clock.UtcNow;
                        gapStart = marketEvent.ReceivedTimeUtc;
                        _status.ReportHeartbeat(key, marketEvent.EventTimeUtc, lastActivity,
                            Math.Max(0, (long)(marketEvent.ReceivedTimeUtc - marketEvent.EventTimeUtc).TotalMilliseconds));
                    }
                    if (subscription.DroppedEvents > lastDropped)
                    {
                        dropped = subscription.DroppedEvents - lastDropped;
                        throw new IOException($"Tick queue overflow: {subscription.DroppedEvents - lastDropped} events dropped.");
                    }
                    lastDropped = subscription.DroppedEvents;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                var now = _clock.UtcNow;
                var start = gapStart ?? now;
                if (start <= now)
                    await RecordGapAsync(instrument, start, now, ex.Message, dropped, token).ConfigureAwait(false);
                gapStart = now;
                dropped = 0;
                _status.ReportReconnect(key, ex.Message, now);
                _logger.LogWarning(ex, "Tick collection interrupted for {InstrumentId}; retrying independently.", instrument.InstrumentId);
            }

            await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            delay = Math.Min(_settings.ReconnectMaximumDelaySeconds, delay * 2);
        }
    }

    private async Task RecordGapAsync(ConfiguredInstrument instrument, DateTime start, DateTime end, string reason, long dropped, CancellationToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        db.TickCoverageGapRecords.Add(new TickCoverageGapRecord
        {
            InstrumentId = instrument.InstrumentId,
            Symbol = instrument.Symbol,
            StartUtc = start,
            EndUtc = end,
            Reason = reason,
            DroppedEvents = dropped,
            RecordedAtUtc = _clock.UtcNow
        });
        await db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private bool IsRegularSession(DateTime utcNow)
    {
        if (!_settings.RegularSessionOnly) return true;
        var eastern = TimeZoneInfo.ConvertTimeFromUtc(utcNow,
            OperatingSystem.IsWindows() ? TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        return eastern.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && eastern.TimeOfDay >= new TimeSpan(_trading.TradingStartHourNewYork, _trading.TradingStartMinuteNewYork, 0)
            && eastern.TimeOfDay < new TimeSpan(_trading.TradingEndHourNewYork, _trading.TradingEndMinuteNewYork, 0);
    }

    private static string Key(string instrumentId) => $"{instrumentId}|TICK|ALL".ToUpperInvariant();
}
