using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

/// <summary>Automatic FX shadow strategy decisions. Deliberately has no order/event-bus/model dependency.</summary>
public sealed class ForexSignalTriggerService
{
    public const string AcceptedStage = "FxShadowSignal";
    private readonly ForexSignalProfile[] _profiles;
    private readonly ForexPatternProfile[] _patterns;
    private readonly IReadOnlyList<ConfiguredInstrument> _instruments;
    private readonly ShortAggregateSettings _aggregates;
    private readonly ILatestMarketDataService _latest;
    private readonly ForexFeatureService _inputs;
    private readonly IFeatureEngine _features;
    private readonly ICandidateResearchService _research;
    private readonly IDbContextFactory<TradingBotDbContext> _db;
    private readonly ILoggerFactory _logs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly Dictionary<string, DateTime> _processed = new();

    public ForexSignalTriggerService(IOptions<ForexSignalSettings> settings, IOptions<ForexPatternSettings> patterns,
        IOptions<TradingSettings> trading, IOptions<ShortAggregateSettings> aggregates, ILatestMarketDataService latest,
        ForexFeatureService inputs, IFeatureEngine features, ICandidateResearchService research,
        IDbContextFactory<TradingBotDbContext> db, ILoggerFactory logs)
    {
        var errors = patterns.Value.Validate();
        if (errors.Count == 0) errors = settings.Value.Validate(trading.Value, aggregates.Value, patterns.Value);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        _profiles = settings.Value.Profiles.Where(x => x.Selection.Enabled).Select(x => x.Snapshot()).ToArray();
        _patterns = patterns.Value.Profiles.Select(x => x.Snapshot()).ToArray();
        _instruments = trading.Value.GetEnabledInstruments();
        _aggregates = aggregates.Value;
        _latest = latest; _inputs = inputs; _features = features; _research = research; _db = db; _logs = logs;
    }

    public async Task<IReadOnlyList<PatternCandidate>> ProcessAsync(string instrumentId, DateTime nowUtc, CancellationToken token = default, Guid? replayRunId = null)
    {
        if (nowUtc.Kind != DateTimeKind.Utc) throw new ArgumentException("FX signal clock must be UTC.", nameof(nowUtc));
        var output = new List<PatternCandidate>();
        foreach (var profile in _profiles.Where(x => x.Selection.InstrumentId == instrumentId))
        {
            var key = $"{replayRunId?.ToString("N") ?? "LIVE"}|{instrumentId}|{profile.Selection.StrategyId}|{profile.Selection.IntervalSeconds}";
            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try { output.AddRange(await ProcessProfileAsync(profile, key, nowUtc, token, replayRunId).ConfigureAwait(false)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (replayRunId.HasValue) throw;
                // A research/storage failure must not reconnect the quote collector or disable other profiles.
                _logs.CreateLogger<ForexSignalTriggerService>().LogError(ex, "FX shadow profile {Profile} failed; uncommitted bar will retry.", key);
            }
            finally { gate.Release(); }
        }
        return output;
    }

    private async Task<IReadOnlyList<PatternCandidate>> ProcessProfileAsync(ForexSignalProfile profile, string key, DateTime now, CancellationToken token, Guid? replayRunId)
    {
        var selection = profile.Selection;
        var instrument = _instruments.Single(x => x.InstrumentId == selection.InstrumentId);
        var pattern = _patterns.Single(x => x.InstrumentId == selection.InstrumentId && x.StrategyId == selection.StrategyId && x.IntervalSeconds == selection.IntervalSeconds);
        var detector = new ForexPatternDetector(pattern, _features, _logs.CreateLogger<PatternDetector>());
        var bars = _latest.GetCompletedForexAggregates(instrument.InstrumentId, selection.IntervalSeconds, now, _aggregates.MaxCompletedPerInterval);
        var output = new List<PatternCandidate>();
        if (bars.Count == 0) return output;
        await using var db = await _db.CreateDbContextAsync(token).ConfigureAwait(false);
        bool hasWatermark;
        lock (_processed) hasWatermark = _processed.ContainsKey(key);
        if (!hasWatermark)
        {
            // All decisions for one bar are committed atomically. Resume this exact contract's last completed bar.
            var featureVersion = _inputs.Compute(instrument.InstrumentId, selection.IntervalSeconds, now)?.FeatureVersion;
            var version = detector.Version + "+" + profile.Version;
            var source = await db.ResearchCandidateRecords.AsNoTracking()
                .Where(x => x.ReplayRunId == replayRunId && x.InstrumentId == instrument.InstrumentId && x.StrategyId == selection.StrategyId
                    && x.Timeframe == selection.Timeframe && x.PatternVersion == version && x.FeatureVersion == featureVersion)
                .OrderByDescending(x => x.SourceEventId).Select(x => x.SourceEventId).FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (source != null && DateTime.TryParseExact(source.Split('|')[^1], "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var resume))
                lock (_processed) _processed[key] = resume;
        }
        // Durable cooldown spans process restarts and config revisions, but is isolated per instrument/strategy/interval.
        var lastSignal = await db.ResearchCandidateRecords.AsNoTracking()
            .Where(x => x.ReplayRunId == replayRunId && x.InstrumentId == instrument.InstrumentId && x.StrategyId == selection.StrategyId
                && x.Timeframe == selection.Timeframe && x.DecisionStage == AcceptedStage && x.Outcome == ResearchCandidateOutcome.Accepted)
            .OrderByDescending(x => x.EvaluatedAtUtc).Select(x => (DateTime?)x.EvaluatedAtUtc).FirstOrDefaultAsync(token).ConfigureAwait(false);
        foreach (var bar in bars)
        {
            DateTime processed;
            lock (_processed) processed = _processed.GetValueOrDefault(key);
            if (bar.WindowEndUtc <= processed) continue;
            var input = _inputs.GetInput(instrument.InstrumentId, selection.IntervalSeconds, bar.AvailableAtUtc);
            if (input == null) continue; // A collector reset can clear history concurrently.
            var features = _features.ComputeFeatures(input);
            var batch = detector.Evaluate(input);
            var blockers = new List<string>();
            if (!MarketDataSession.IsForexOpen(now)) blockers.Add("FX collection session is closed.");
            if (now - bar.WindowEndUtc > TimeSpan.FromSeconds(selection.MaximumSignalAgeSeconds)) blockers.Add("Signal exceeded maximum age.");
            if (bar.LastQuoteTimeUtc > now || now - bar.LastQuoteTimeUtc > TimeSpan.FromSeconds(profile.MaximumQuoteAgeSeconds)) blockers.Add("Paired quote is stale or in the future.");
            var mid = (bar.LastBid + bar.LastAsk) / 2m;
            var spreadBps = mid > 0m ? (bar.LastAsk - bar.LastBid) / mid * 10000m : decimal.MaxValue;
            if (bar.LastBid <= 0m || bar.LastAsk < bar.LastBid || spreadBps > profile.MaximumSpreadBps) blockers.Add("Paired bid/ask spread exceeds the FX profile limit or is invalid.");
            if (features.SampleCount < selection.MinimumBars || features.PriceFeaturesReady != true) blockers.Add("Consecutive price feature warmup is incomplete.");
            if (features.AtrToPriceRatio is not >= 0m || features.AtrToPriceRatio > profile.MaximumAtrToPriceRatio) blockers.Add("ATR/price is unavailable or exceeds the FX volatility limit.");
            if (profile.ApprovedSignalVersion != profile.Version || profile.ApprovedPatternVersion != detector.Version || profile.ApprovedFeatureVersion != features.FeatureVersion)
                blockers.Add("Shadow approval does not match the instrument/strategy signal, pattern and feature versions.");
            if (lastSignal.HasValue && now - lastSignal.Value < TimeSpan.FromSeconds(selection.CooldownSeconds)) blockers.Add("Profile cooldown is active.");

            var metadata = new Dictionary<string, string>
            {
                ["signalVersion"] = profile.Version, ["patternProfileVersion"] = detector.Version, ["shadowOnly"] = "true",
                ["signalProfile"] = JsonSerializer.Serialize(profile), ["decisionAtUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
                ["pairedQuoteTimeUtc"] = bar.LastQuoteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                ["pairedSpreadBps"] = spreadBps.ToString("G29", CultureInfo.InvariantCulture),
                ["atrToPriceRatio"] = features.AtrToPriceRatio?.ToString("G29", CultureInfo.InvariantCulture) ?? "unavailable"
            };
            var evaluations = batch.Evaluations.Select(x => WithSignalVersion(x, profile.Version, metadata)).ToArray();
            var selected = blockers.Count == 0 ? PatternSignalSelection.Select(
                evaluations.Where(x => x.Candidate != null && instrument.AllowedDirections.Contains(x.Direction)).Select(x => x.Candidate!), selection) : null;
            var decisions = evaluations.ToDictionary(x => x.EvaluationKey, x =>
                x.Candidate == null ? new PatternSignalDecision("PatternHardConditions", blockers) :
                !selection.PatternTypes.Contains(x.PatternType) ? new PatternSignalDecision("FxPatternFilter", ["Pattern is not enabled for this signal profile."]) :
                !instrument.AllowedDirections.Contains(x.Direction) ? new PatternSignalDecision("FxDirection", ["Direction is not enabled for this instrument."]) :
                blockers.Count > 0 ? new PatternSignalDecision("FxSignalGate", blockers) :
                x.Candidate.Confidence < selection.MinimumConfidence ? new PatternSignalDecision("FxConfidence", ["Candidate is below the signal confidence threshold."]) :
                selected?.PatternKey != x.EvaluationKey ? new PatternSignalDecision("FxRanking", ["A higher-ranked candidate was selected for this bar."]) :
                new PatternSignalDecision(AcceptedStage, ["Signal approval only; any paper entry separately requires an approved FX model and execution gates."]));
            var filtered = new PatternDetectionBatch { Evaluations = evaluations, Candidates = selected == null ? [] : [selected], SignalDecisions = decisions };
            var added = await _research.PersistBatchAsync(filtered,
                $"FX-SHADOW|{selection.InstrumentId}|{selection.StrategyId}|{selection.IntervalSeconds}|{bar.WindowEndUtc:O}", replayRunId: replayRunId, evaluationContext:
                new CandidateEvaluationContext(features.Regime, null, features.AtrToPriceRatio, now,
                    bar.LastBid, bar.LastQuoteTimeUtc, bar.LastAsk, bar.LastQuoteTimeUtc), cancellationToken: token).ConfigureAwait(false);
            if (selected != null && added > 0) { output.Add(selected); lastSignal = now; }
            lock (_processed) _processed[key] = bar.WindowEndUtc;
        }
        return output;
    }

    private static PatternEvaluation WithSignalVersion(PatternEvaluation x, string signalVersion, Dictionary<string, string> extra)
    {
        var version = x.PatternVersion + "+" + signalVersion;
        var metadata = new Dictionary<string, string>(x.Candidate?.Metadata ?? x.Metadata);
        foreach (var entry in extra) metadata[entry.Key] = entry.Value;
        metadata["patternVersion"] = version;
        var context = PipelineContext.CreateForSignal(x.InstrumentId,
            $"{x.Direction}|{x.PatternType}|{x.Timeframe}|{x.EvaluatedAtUtc:O}|{version}|{x.FeatureVersion}", x.StrategyId, x.FeatureVersion, version);
        context = new PipelineContext(context.CorrelationId, context.SignalId, context.InstrumentId, context.StrategyId,
            context.MarketDataVersion, context.FeatureVersion, context.PatternVersion, signalVersion);
        var candidate = x.Candidate == null ? null : new PatternCandidate(x.PatternType, x.Symbol, x.Timeframe, x.EvaluatedAtUtc,
            x.Candidate.Confidence, x.Candidate.RelevantPriceLevels, metadata, context, x.Direction, x.HardConditions, x.ScoreComponents, x.Reasons);
        return new PatternEvaluation { InstrumentId = x.InstrumentId, Symbol = x.Symbol, StrategyId = x.StrategyId,
            PatternType = x.PatternType, Direction = x.Direction, Timeframe = x.Timeframe, EvaluatedAtUtc = x.EvaluatedAtUtc,
            ReferencePrice = x.ReferencePrice, PatternVersion = version, FeatureVersion = x.FeatureVersion, Candidate = candidate,
            HardConditions = x.HardConditions, ScoreComponents = x.ScoreComponents, Reasons = x.Reasons, Metadata = metadata };
    }
}
