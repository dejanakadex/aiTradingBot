using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services;

/// <summary>Turns completed short trade bars into existing pattern candidates; never submits orders.</summary>
public sealed class ShortSignalTriggerService
{
    private readonly ShortSignalSettings _settings;
    private readonly ILatestMarketDataService _latest;
    private readonly IPatternDetectorFactory _detectors;
    private readonly IFeatureEngine _features;
    private readonly ITradingEventBus _events;
    private readonly ICandidateResearchService? _research;
    private readonly ILogger<ShortSignalTriggerService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instrumentLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProfileState> _states = new(StringComparer.OrdinalIgnoreCase);

    public ShortSignalTriggerService(
        IOptions<ShortSignalSettings> settings,
        IOptions<TradingSettings> trading,
        IOptions<ShortAggregateSettings> aggregates,
        ILatestMarketDataService latest,
        IPatternDetectorFactory detectors,
        IFeatureEngine features,
        ITradingEventBus events,
        ILogger<ShortSignalTriggerService> logger,
        ICandidateResearchService? research = null)
    {
        _settings = settings.Value;
        var errors = _settings.GetValidationErrors(trading.Value, aggregates.Value);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        _latest = latest;
        _detectors = detectors;
        _features = features;
        _events = events;
        _logger = logger;
        _research = research;
    }

    public async Task ProcessAsync(ConfiguredInstrument instrument, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var profiles = (_settings.Profiles ?? [])
            .Where(profile => profile.Enabled
                && profile.InstrumentId.Equals(instrument.InstrumentId, StringComparison.OrdinalIgnoreCase)
                && instrument.StrategyIds.Contains(profile.StrategyId, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (profiles.Length == 0) return;

        var gate = _instrumentLocks.GetOrAdd(instrument.InstrumentId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var profile in profiles)
                await ProcessProfileAsync(instrument, profile, ToUtc(nowUtc), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task ProcessProfileAsync(ConfiguredInstrument instrument, ShortSignalProfile profile, DateTime nowUtc, CancellationToken token)
    {
        var key = $"{instrument.InstrumentId}|{profile.StrategyId}|{profile.IntervalSeconds}";
        var state = _states.GetOrAdd(key, _ => new ProfileState());
        var completed = _latest.GetCompletedShortAggregates(instrument.InstrumentId, profile.IntervalSeconds, nowUtc, 80);
        for (var index = 0; index < completed.Count; index++)
        {
            var bar = completed[index];
            if (bar.WindowEndUtc <= state.LastProcessedEndUtc) continue;
            if (nowUtc - bar.WindowEndUtc > TimeSpan.FromSeconds(profile.MaximumSignalAgeSeconds))
            {
                state.LastProcessedEndUtc = bar.WindowEndUtc;
                continue;
            }
            var series = ConsecutiveTail(completed, index, instrument, profile);
            if (series.Count < profile.MinimumBars)
            {
                state.LastProcessedEndUtc = bar.WindowEndUtc;
                continue;
            }

            var batch = _detectors.Create().Process(new PatternDetectionInput
            {
                Candles = series,
                StrategyId = profile.StrategyId,
                FeatureVersion = $"{_features.FeatureVersion}+short-{profile.IntervalSeconds}s-v1",
                Directions = instrument.AllowedDirections
            });
            var allowed = profile.PatternTypes.ToHashSet();
            var selected = batch.Candidates
                .Where(candidate => allowed.Contains(candidate.PatternType)
                    && candidate.Confidence >= profile.MinimumConfidence)
                .OrderByDescending(candidate => candidate.Confidence)
                .ThenBy(candidate => candidate.PatternType)
                .FirstOrDefault();
            if (state.LastPublishedEndUtc != default
                && bar.WindowEndUtc - state.LastPublishedEndUtc < TimeSpan.FromSeconds(profile.CooldownSeconds))
                selected = null;

            var filtered = new PatternDetectionBatch
            {
                Evaluations = batch.Evaluations.Where(evaluation => allowed.Contains(evaluation.PatternType)).ToArray(),
                Candidates = selected == null ? [] : [selected]
            };
            if (_research != null)
                await _research.PersistBatchAsync(filtered,
                    $"LIVE-SHORT|{instrument.InstrumentId}|{profile.IntervalSeconds}s|{bar.WindowEndUtc:O}",
                    cancellationToken: token).ConfigureAwait(false);

            if (selected != null)
            {
                if (!await _events.PublishAsync(selected, token).ConfigureAwait(false))
                    throw new IOException($"Short signal channel rejected {selected.PatternKey}.");
                state.LastPublishedEndUtc = bar.WindowEndUtc;
                _logger.LogInformation("Short signal {Pattern} for {InstrumentId}/{StrategyId} at {WindowEndUtc}",
                    selected.PatternType, instrument.InstrumentId, profile.StrategyId, bar.WindowEndUtc);
            }
            state.LastProcessedEndUtc = bar.WindowEndUtc;
        }
    }

    private static IReadOnlyList<Candle> ConsecutiveTail(
        IReadOnlyList<ShortIntervalBarSnapshot> completed, int lastIndex,
        ConfiguredInstrument instrument, ShortSignalProfile profile)
    {
        var tail = new List<Candle>();
        for (var index = lastIndex; index >= 0 && tail.Count < 80; index--)
        {
            if (index < lastIndex && completed[index + 1].EmptyIntervalsBefore > 0) break;
            var bar = completed[index];
            tail.Add(new Candle(instrument.Symbol, profile.Timeframe, bar.WindowEndUtc,
                bar.Open, bar.High, bar.Low, bar.Close, bar.Volume,
                instrument.InstrumentId, bar.AvailableAtUtc, "ShortAggregates.Trade"));
        }
        tail.Reverse();
        return tail;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime()
    };

    private sealed class ProfileState
    {
        public DateTime LastProcessedEndUtc { get; set; }
        public DateTime LastPublishedEndUtc { get; set; }
    }
}
