using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Background;

/// <summary>Read-only minute summaries. Does not advance/finalize bars or depend on trading/signal approval.</summary>
public sealed class ForexDiagnosticsHostedService(ILatestMarketDataService latest, IIbkrConnectionService connection,
    IOptions<TradingSettings> trading, IOptions<ShortAggregateSettings> settings, IClock clock,
    ILogger<ForexDiagnosticsHostedService> logger) : BackgroundService
{
    private readonly Dictionary<string, ForexAggregateDiagnostics> _previous = new();
    private DateTime? _lastReport;

    public void Report(DateTime nowUtc)
    {
        var elapsed = _lastReport.HasValue ? (nowUtc - _lastReport.Value).TotalSeconds : (double?)null;
        foreach (var instrument in trading.Value.GetEnabledInstruments().Where(x => x.InstrumentType == InstrumentType.Forex))
        {
            var current = latest.GetForexDiagnostics(instrument.InstrumentId);
            var previous = _previous.GetValueOrDefault(instrument.InstrumentId) ?? new(0, 0, 0);
            var intervals = settings.Value.IntervalsSeconds.Order().Select(interval =>
            {
                var bars = latest.GetCompletedForexAggregates(instrument.InstrumentId, interval, nowUtc, settings.Value.MaxCompletedPerInterval);
                var consecutive = bars.Count == 0 ? 0 : 1;
                for (var i = bars.Count - 1; i > 0 && bars[i].WindowStartUtc == bars[i - 1].WindowEndUtc; i--) consecutive++;
                var stats = current.Windows.GetValueOrDefault(interval) ?? new(0, 0);
                var before = previous.Windows.GetValueOrDefault(interval) ?? new(0, 0);
                return new
                {
                    IntervalSeconds = interval, CompletedDelta = stats.Completed - before.Completed,
                    SuppressedDelta = stats.Suppressed - before.Suppressed, ConsecutiveBars = consecutive,
                    LastBarEndUtc = bars.LastOrDefault()?.WindowEndUtc,
                    LastBarAgeSeconds = bars.Count == 0 ? (double?)null : (nowUtc - bars[^1].WindowEndUtc).TotalSeconds,
                    NoRecentBar = bars.Count == 0 || nowUtc - bars[^1].WindowEndUtc > TimeSpan.FromSeconds(60),
                    EmptyIntervalsBeforeLastBar = bars.LastOrDefault()?.EmptyIntervalsBefore,
                    RetentionLimit = settings.Value.MaxCompletedPerInterval
                };
            }).ToArray();
            var rejected = current.RejectedQuoteEvents - previous.RejectedQuoteEvents;
            var late = current.LateQuotes - previous.LateQuotes;
            var incomplete = current.IncompleteQuotes - previous.IncompleteQuotes;
            var open = MarketDataSession.IsForexOpen(nowUtc);
            var level = rejected > 0 || late > 0 || incomplete > 0 || intervals.Any(x => x.SuppressedDelta > 0)
                || open && (connection.Status != ConnectionStatus.Connected || intervals.Any(x => x.NoRecentBar)) ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level,
                "FX collection summary Instrument={Instrument} AsOfUtc={AsOfUtc:O} ElapsedSeconds={ElapsedSeconds} Connection={Connection} SessionOpen={SessionOpen} RejectedEventsDelta={Rejected} FutureEventsDelta={Future} StaleEventsDelta={Stale} OtherRejectedEventsDelta={Other} LatePairsDelta={Late} IncompletePairsDelta={Incomplete} ResetsDelta={Resets} Intervals={Intervals}",
                instrument.InstrumentId, nowUtc, elapsed, connection.Status, open, rejected,
                current.FutureQuoteEvents - previous.FutureQuoteEvents, current.StaleQuoteEvents - previous.StaleQuoteEvents,
                rejected - (current.FutureQuoteEvents - previous.FutureQuoteEvents) - (current.StaleQuoteEvents - previous.StaleQuoteEvents),
                late, incomplete, current.ResetCount - previous.ResetCount, JsonSerializer.Serialize(intervals));
            _previous[instrument.InstrumentId] = current;
        }
        _lastReport = nowUtc;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!trading.Value.GetEnabledInstruments().Any(x => x.InstrumentType == InstrumentType.Forex)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { Report(clock.UtcNow); }
                catch (Exception ex) { logger.LogError(ex, "FX collection diagnostics failed; collection remains independent."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
