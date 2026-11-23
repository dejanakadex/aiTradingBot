using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

public sealed partial class MarketDataQualityService
{
    // At most three fixed keys per configured instrument; no queue of healthy events.
    private sealed class CheckpointState(MarketDataStreamStateRecord state, bool exists)
    {
        public readonly object Sync = new();
        public MarketDataStreamStateRecord State = state;
        public int PersistedVersion = state.Version;
        public bool Exists = exists;
        public long? DirtySince;
        public bool Failed;
        public MarketDataQualityIncidentRecord? Incident;
    }
    private sealed record Checkpoint(CheckpointState Owner, MarketDataStreamStateRecord State,
        MarketDataQualityIncidentRecord? Incident, long CapturedAt);
    private readonly ConcurrentDictionary<string, CheckpointState> _checkpointStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _checkpointWriter = new(1, 1);
    private HashSet<string> _checkpointInstruments = new(StringComparer.OrdinalIgnoreCase);
    private TimeProvider _checkpointTime = TimeProvider.System;
    private bool _checkpointsEnabled;
    private TimeSpan _checkpointInterval;
    private double _maximumCheckpointLag;
    private int _checkpointStopping;
    private long _persistedStreamUpdates;
    private readonly object _checkpointMetrics = new();
    private DateTime? _lastCheckpointUtc;
    private double _lastCheckpointMilliseconds;
    private double _maximumCheckpointMilliseconds;
    private double _maximumObservedPendingMilliseconds;

    private void InitializeCheckpoints(MarketDataCollectionSettings config, TimeProvider time)
    {
        if (config.GetValidationErrors().Count != 0) throw new ArgumentException("Invalid market-data collection settings.");
        _checkpointTime = time;
        _checkpointInterval = TimeSpan.FromMilliseconds(config.QualityStateCheckpointMilliseconds);
        _maximumCheckpointLag = config.QualityStateMaximumLagMilliseconds;
        _checkpointsEnabled = config.QualityStateCheckpointMilliseconds > 0 && !_settings.Enabled
            && _settings.OperatingMode == TradingOperatingMode.AnalysisOnly
            && !_settings.ForexPaperExecutionEnabled && !_settings.LiveTradingExplicitlyEnabled;
        _checkpointInstruments = _settings.GetConfiguredInstruments().Select(x => x.InstrumentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (config.QualityStateCheckpointMilliseconds > 0 && !_checkpointsEnabled)
            _logger.LogWarning("Quality checkpoints require trading-disabled AnalysisOnly mode; using durable per-event writes.");
    }

    private bool UsesCheckpointState(CanonicalMarketDataEvent e) => _checkpointsEnabled
        && _checkpointInstruments.Contains(e.InstrumentId) && e.Timeframe == string.Empty
        && e.Kind is MarketDataEventKind.Bid or MarketDataEventKind.Ask or MarketDataEventKind.Trade;

    private bool CheckpointModeAllowed => !_settings.Enabled && _settings.OperatingMode == TradingOperatingMode.AnalysisOnly
        && !_settings.ForexPaperExecutionEnabled && !_settings.LiveTradingExplicitlyEnabled;

    private double PendingAge(CheckpointState state)
    {
        var age = state.DirtySince.HasValue ? _checkpointTime.GetElapsedTime(state.DirtySince.Value).TotalMilliseconds : 0;
        lock (_checkpointMetrics) _maximumObservedPendingMilliseconds = Math.Max(_maximumObservedPendingMilliseconds, age);
        return age;
    }

    private async Task<MarketDataQualityAssessment> EvaluateCheckpointedAsync(CanonicalMarketDataEvent e,
        bool allowStaleHistoricalData, CancellationToken ct)
    {
        var gate = _streamLocks.GetOrAdd(e.StreamKey, _ => new SemaphoreSlim(1, 1));
        using (CollectionTiming.Measure(CollectionPhase.QualityGate)) await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _checkpointStopping) != 0) throw new InvalidOperationException("Quality collection is stopping.");
            if (!CheckpointModeAllowed) throw new InvalidOperationException("Quality checkpoint mode changed; restart to use per-event trading persistence.");
            var first = !_checkpointStates.TryGetValue(e.StreamKey, out var state);
            if (first)
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
                MarketDataStreamStateRecord? stored;
                using (CollectionTiming.Measure(CollectionPhase.QualityRead))
                    stored = await db.MarketDataStreamStateRecords.AsNoTracking().SingleOrDefaultAsync(x => x.StreamKey == e.StreamKey, ct).ConfigureAwait(false);
                state = new CheckpointState(stored ?? CreateState(e, e.StreamKey, ToUtc(_clock.UtcNow)), stored != null);
                _checkpointStates[e.StreamKey] = state;
            }
            MarketDataQualityAssessment assessment;
            Checkpoint snapshot;
            lock (state!.Sync)
            {
                if (state.Failed || state.Incident != null || PendingAge(state) > _maximumCheckpointLag)
                    throw new IOException("Quality checkpoint is failed or overdue; collection is blocked until persistence recovers.");
                using (CollectionTiming.Measure(CollectionPhase.QualityAssess)) assessment = Assess(e, state.State, allowStaleHistoricalData);
                var now = ToUtc(_clock.UtcNow);
                ApplyAssessment(state.State, e, assessment, now);
                state.DirtySince ??= _checkpointTime.GetTimestamp();
                if (!assessment.IsHealthy)
                    state.Incident = new MarketDataQualityIncidentRecord
                    {
                        EventId = e.EventId, StreamKey = e.StreamKey, InstrumentId = e.InstrumentId, Symbol = e.Symbol,
                        Kind = e.Kind, Timeframe = e.Timeframe, Source = e.Source, EventTimeUtc = ToUtc(e.EventTimeUtc),
                        ReceivedTimeUtc = ToUtc(e.ReceivedTimeUtc), Sequence = e.Sequence, Status = assessment.Status,
                        Reason = assessment.Reason, RecordedAtUtc = now
                    };
                snapshot = Capture(state);
            }
            // Incidents, initial watermarks and explicit sequence contracts remain synchronous.
            // Only healthy, unsequenced live tick state may be coalesced.
            if (first || !assessment.IsHealthy || e.Sequence.HasValue || !e.Source.StartsWith("IBKR.TickByTick", StringComparison.Ordinal))
            {
                try
                {
                    using (CollectionTiming.Measure(CollectionPhase.QualitySave))
                        await PersistCheckpointsAsync([snapshot], ct).ConfigureAwait(false);
                }
                catch { lock (state.Sync) state.Failed = true; throw; }
            }
            if (!assessment.IsHealthy)
                _logger.LogWarning("Market data quality {Status} for {InstrumentId} {Kind} {Timeframe}: {Reason}",
                    assessment.Status, e.InstrumentId, e.Kind, e.Timeframe, assessment.Reason);
            return assessment;
        }
        finally { gate.Release(); }
    }

    private Checkpoint Capture(CheckpointState state) => new(state, CopyState(state.State), state.Incident, _checkpointTime.GetTimestamp());

    // An explicit drain is useful for shutdown and deterministic tests. Serialized with immediate incident writes.
    public Task FlushCheckpointsAsync(CancellationToken ct = default)
    {
        var snapshots = new List<Checkpoint>();
        foreach (var state in _checkpointStates.Values)
            lock (state.Sync)
                if (state.DirtySince.HasValue) snapshots.Add(Capture(state));
        return PersistCheckpointsAsync(snapshots, ct);
    }

    private async Task PersistCheckpointsAsync(IReadOnlyList<Checkpoint> snapshots, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;
        await _checkpointWriter.WaitAsync(ct).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Once a write starts, finish it even if shutdown was requested. The host still bounds its wait.
            await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            var acknowledged = new List<(Checkpoint Snapshot, int Version)>();
            var updates = 0;
            foreach (var snapshot in snapshots)
            {
                int durableVersion; bool exists;
                lock (snapshot.Owner.Sync) { durableVersion = snapshot.Owner.PersistedVersion; exists = snapshot.Owner.Exists; }
                var record = await db.MarketDataStreamStateRecords.SingleOrDefaultAsync(x => x.StreamKey == snapshot.State.StreamKey).ConfigureAwait(false);
                if ((record != null) != exists || record != null && record.Version != durableVersion)
                    throw new DbUpdateConcurrencyException("Quality checkpoint was modified by another writer; refusing to overwrite its state.");
                if (snapshot.State.Version > durableVersion)
                {
                    var copy = CopyState(snapshot.State);
                    copy.Id = record?.Id ?? 0;
                    if (record == null) db.MarketDataStreamStateRecords.Add(copy);
                    else db.Entry(record).CurrentValues.SetValues(copy);
                    updates++;
                }
                if (snapshot.Incident is { } incident)
                {
                    // A snapshot captured before an immediate write may arrive after it; don't duplicate that incident.
                    bool stillPending;
                    lock (snapshot.Owner.Sync) stillPending = ReferenceEquals(snapshot.Owner.Incident, incident);
                    if (stillPending)
                    {
                        var copy = new MarketDataQualityIncidentRecord();
                        db.Entry(copy).CurrentValues.SetValues(incident);
                        copy.Id = 0;
                        db.MarketDataQualityIncidentRecords.Add(copy);
                    }
                }
                acknowledged.Add((snapshot, Math.Max(snapshot.State.Version, durableVersion)));
            }
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var (snapshot, version) in acknowledged)
                lock (snapshot.Owner.Sync)
                {
                    var state = snapshot.Owner;
                    var previousVersion = state.PersistedVersion;
                    state.PersistedVersion = version;
                    state.Exists = true;
                    if (ReferenceEquals(state.Incident, snapshot.Incident)) state.Incident = null;
                    if (state.State.Version <= version) { state.DirtySince = null; state.Failed = false; }
                    else if (version > previousVersion) state.DirtySince = snapshot.CapturedAt;
                }
            Interlocked.Add(ref _persistedStreamUpdates, updates);
            lock (_checkpointMetrics)
            {
                _lastCheckpointUtc = ToUtc(_clock.UtcNow);
                _lastCheckpointMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                _maximumCheckpointMilliseconds = Math.Max(_maximumCheckpointMilliseconds, _lastCheckpointMilliseconds);
            }
        }
        catch
        {
            foreach (var snapshot in snapshots) lock (snapshot.Owner.Sync) snapshot.Owner.Failed = true;
            throw;
        }
        finally { _checkpointWriter.Release(); }
    }

    private IEnumerable<MarketDataStreamSnapshot> CachedSnapshots()
    {
        foreach (var state in _checkpointStates.Values)
        {
            MarketDataStreamSnapshot snapshot;
            lock (state.Sync)
            {
                snapshot = ToSnapshot(state.State);
                if (!CheckpointModeAllowed || state.Failed || PendingAge(state) > _maximumCheckpointLag)
                    snapshot = snapshot with { IsHealthy = false, Status = MarketDataQualityStatus.Invalid,
                        StatusReason = "Quality checkpoint is failed or overdue; collection is blocked until persistence recovers." };
            }
            yield return snapshot;
        }
    }

    public QualityPersistenceSnapshot GetPersistenceStatus()
    {
        int pending = 0, failed = 0; double age = 0;
        foreach (var state in _checkpointStates.Values)
            lock (state.Sync)
            {
                if (state.DirtySince.HasValue) pending++;
                age = Math.Max(age, PendingAge(state));
                if (state.Failed) failed++;
            }
        lock (_checkpointMetrics)
            return new(_checkpointsEnabled, _checkpointStates.Count, pending, failed, age,
                Interlocked.Read(ref _persistedStreamUpdates), _lastCheckpointUtc, _lastCheckpointMilliseconds,
                Volatile.Read(ref _checkpointStopping) == 0 && (!_checkpointsEnabled || CheckpointModeAllowed)
                    && failed == 0 && age <= _maximumCheckpointLag, _maximumCheckpointMilliseconds, _maximumObservedPendingMilliseconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_checkpointsEnabled) return;
        var report = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                await Task.Delay(_checkpointInterval, _checkpointTime, stoppingToken).ConfigureAwait(false);
                try { await FlushCheckpointsAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "Quality checkpoint failed; affected streams are blocked until a successful retry."); }
                if (report.Elapsed >= TimeSpan.FromMinutes(1))
                {
                    _logger.LogInformation("Quality checkpoint AsOfUtc={AsOfUtc:O} Metrics={Metrics}",
                        _clock.UtcNow, JsonSerializer.Serialize(GetPersistenceStatus()));
                    report.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_checkpointsEnabled) { await base.StopAsync(cancellationToken).ConfigureAwait(false); return; }
        Interlocked.Exchange(ref _checkpointStopping, 1);
        // Finish in-flight assessments before the final checkpoint; new assessments now fail closed.
        foreach (var gate in _streamLocks.Values)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gate.Release();
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try { await FlushCheckpointsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogCritical(ex, "Final quality checkpoint failed; restart coverage must include the uncheckpointed tail."); throw; }
    }

    private static MarketDataStreamStateRecord CopyState(MarketDataStreamStateRecord x) => new()
    {
        Id = x.Id, StreamKey = x.StreamKey, InstrumentId = x.InstrumentId, Symbol = x.Symbol, Kind = x.Kind,
        Timeframe = x.Timeframe, Source = x.Source, LastEventId = x.LastEventId, LastEventTimeUtc = x.LastEventTimeUtc,
        LastReceivedTimeUtc = x.LastReceivedTimeUtc, LastSequence = x.LastSequence, Status = x.Status,
        StatusReason = x.StatusReason, IsHealthy = x.IsHealthy, UpdatedAtUtc = x.UpdatedAtUtc, Version = x.Version
    };
}
