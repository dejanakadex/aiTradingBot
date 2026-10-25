using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

/// <summary>FX onboarding evidence; PaperReady never grants execution permission or live approval.</summary>
public sealed class ForexRolloutService(IDbContextFactory<TradingBotDbContext> factory,
    IInstrumentRegistryService registry, ForexResearchContract contracts, ForexFeatureService features,
    ILatestMarketDataService latest, ITradingEngineStatusService engine,
    IOptions<InstrumentRolloutSettings> settings, IOptions<ResearchEvaluationSettings> evaluationSettings,
    IOptions<ShadowPerformanceSettings> performance, IOptions<ForexSignalSettings> signals, IClock clock)
{
    public async Task<InstrumentRolloutEvaluation> EvaluateAsync(InstrumentRegistrySnapshot instrument, bool apply, CancellationToken ct)
    {
        var s = settings.Value;
        var now = clock.UtcNow;
        var from = now.AddHours(-s.LookbackHours);
        var shadowFrom = instrument.StatusChangedAtUtc > from ? instrument.StatusChangedAtUtc : from;
        var reasons = new List<string>();
        var critical = new List<string>();
        await using var db = await factory.CreateDbContextAsync(ct);
        var jobs = await db.HistoricalBackfillJobRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId).ToListAsync(ct);
        bool historyComplete = instrument.MarketDataTimeframes.Count > 0, historyReady = historyComplete;
        foreach (var timeframe in instrument.MarketDataTimeframes)
        {
            var job = jobs.SingleOrDefault(x => x.Timeframe == timeframe);
            if (job?.Status is not (HistoricalBackfillStatus.Completed or HistoricalBackfillStatus.CompletedWithGaps))
            { historyReady = historyComplete = false; reasons.Add($"History {timeframe}: {job?.Status.ToString() ?? "not planned"}."); continue; }
            var tf = timeframe switch { "1m" => Timeframe.OneMinute, "5m" => Timeframe.FiveMinutes, "15m" => Timeframe.FifteenMinutes, _ => (Timeframe)(-1) };
            var bars = await db.Candles.CountAsync(x => x.InstrumentId == instrument.InstrumentId && x.Timeframe == tf
                && x.TimestampUtc >= job.DesiredStartUtc && x.TimestampUtc < job.DesiredEndUtc && x.IsFinal
                && x.QualityStatus == MarketDataQualityStatus.Healthy && x.Source.StartsWith("IBKR.Midpoint") && x.Volume == null, ct);
            if (job.Status != HistoricalBackfillStatus.Completed || job.GapCount != 0 || bars < s.MinimumFinalBarsPerTimeframe)
            { historyReady = false; reasons.Add($"History {timeframe}: gaps={job.GapCount}; healthy MIDPOINT bars={bars}/{s.MinimumFinalBarsPerTimeframe}."); }
        }
        var streams = await db.MarketDataStreamStateRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId).ToListAsync(ct);
        var incidents = await db.MarketDataQualityIncidentRecords.CountAsync(x => x.InstrumentId == instrument.InstrumentId && x.RecordedAtUtc >= from, ct);
        var gaps = await db.TickCoverageGapRecords.CountAsync(x => x.InstrumentId == instrument.InstrumentId && x.EndUtc >= from && x.StartUtc <= now, ct);
        var orderIds = await db.SignalArbitrationRecords.Where(x => x.InstrumentId == instrument.InstrumentId && x.EntryBrokerOrderId != null)
            .Select(x => x.EntryBrokerOrderId).ToArrayAsync(ct);
        var unknownOrders = await db.OrderRecords.CountAsync(x => orderIds.Contains(x.BrokerOrderId)
            && (x.Status == OrderStatus.Unknown || x.Status == OrderStatus.PendingBrokerConfirmation || x.Status == OrderStatus.CancelPending), ct);
        if (streams.Count == 0 || streams.Any(x => !x.IsHealthy)) critical.Add("FX market-data streams are missing or unhealthy.");
        if (incidents > s.MaximumRecentQualityIncidents) critical.Add("Recent FX market-data quality incidents exceed the threshold.");
        if (gaps > 0) critical.Add($"FX tick coverage has {gaps} gap(s) in the observation window; MIDPOINT backfill does not repair them.");
        if (unknownOrders > 0) critical.Add($"{unknownOrders} FX order(s) have unresolved lifecycle state.");
        var quote = latest.Get(instrument.InstrumentId);
        if (quote?.Bid is not > 0 || quote.Ask is not > 0 || quote.Ask < quote.Bid || quote.BidTimeUtc == null
            || quote.BidTimeUtc != quote.AskTimeUtc || quote.BidTimeUtc > now || now - quote.BidTimeUtc > TimeSpan.FromSeconds(3))
            critical.Add("Fresh paired FX bid/ask is unavailable; MIDPOINT history cannot replace quote ticks.");
        if (!MarketDataSession.IsForexOpen(now)) critical.Add("FX session is closed; collection will resume independently of trading permission.");

        var researchReady = historyReady && instrument.StrategyIds.Count > 0;
        var shadowReady = true;
        var sampleReady = true;
        int count = 0, approved = 0;
        var raw = await db.ScalpingExecutionDecisionRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId
            && x.SelectedPolicy == ForexShadowService.Policy && x.EvaluatedAtUtc >= shadowFrom && x.EvaluatedAtUtc <= now).ToListAsync(ct);
        foreach (var strategy in instrument.StrategyIds)
        {
            try
            {
                var c = contracts.Get(instrument.InstrumentId, strategy);
                // A completed, unchanged replay is required: live deferred labels and stock labels are not FX execution evidence.
                var runs = await db.ReplayRunRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId
                    && x.StrategyId == strategy && x.Status == ReplayRunStatus.Completed && x.FeatureVersion == c.Feature && x.PatternVersion == c.Pattern
                    && x.StrategyVersion == c.Label).ToListAsync(ct);
                var valid = new HashSet<Guid>();
                foreach (var run in runs)
                    if (run.InputSha256.Length > 0 && run.OutputSha256 == await DeterministicReplayService.ComputeOutputHashAsync(db, run.Id, ct)) valid.Add(run.Id);
                var labels = await db.CandidateLabelRecords.AsNoTracking().Where(x => x.ResearchCandidate.InstrumentId == instrument.InstrumentId
                    && x.ResearchCandidate.StrategyId == strategy && x.ResearchCandidate.ReplayRunId != null
                    && valid.Contains(x.ResearchCandidate.ReplayRunId.Value) && x.Status == CandidateLabelStatus.Complete
                    && x.LabelVersion == c.Label && x.HorizonSeconds == c.Profile.HoldingSeconds && x.NetReturnBps != null && x.InputSha256 != "")
                    .Select(x => x.ResearchCandidate.CandidateKey).Distinct().CountAsync(ct);
                if (labels < s.MinimumCompleteLabelsPerStrategy)
                { researchReady = false; reasons.Add($"{strategy}: distinct intact FX execution labels {labels}/{s.MinimumCompleteLabelsPerStrategy}."); }
                var evaluations = await db.ResearchEvaluationRunRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId
                    && x.StrategyId == strategy && x.ReplayRunId != null && valid.Contains(x.ReplayRunId.Value)
                    && x.FeatureVersion == c.Feature && x.PatternVersion == c.Pattern && x.LabelVersion == c.Label
                    && x.HorizonSeconds == c.Profile.HoldingSeconds).ToListAsync(ct);
                var evaluationIds = evaluations.Where(x => x.CompletedAtUtc != default && x.InputSha256.Length > 0
                    && x.EvaluationVersion == ResearchEvaluationVersion.Create(evaluationSettings.Value)
                    && x.OutputSha256 == ResearchEvaluationService.ComputeOutputHash(x)).Select(x => x.Id).ToHashSet();
                var models = await db.NumericalModelRecords.AsNoTracking().Where(x => x.InstrumentId == instrument.InstrumentId && x.StrategyId == strategy
                    && x.Status == NumericalModelStatus.Approved).ToListAsync(ct);
                var compatible = models.Where(x => evaluationIds.Contains(x.EvaluationRunId) && x.ApprovalReady
                    && x.ModelVersionTag == ForexResearchContract.Model && x.FeatureVersion == c.Feature && x.PatternVersion == c.Pattern
                    && x.LabelVersion == c.Label && x.HorizonSeconds == c.Profile.HoldingSeconds && x.ModelArtifact.Length > 0
                    && x.OutputSha256 == NumericalModelService.OutputHash(x)).ToArray();
                if (compatible.Length != 1)
                { shadowReady = false; reasons.Add($"{strategy}: one approved compatible FX model and intact temporal evaluation are required."); }
                var f = features.Compute(instrument.InstrumentId, c.Profile.IntervalSeconds, now);
                var signal = signals.Value.Profiles.SingleOrDefault(x => x.Selection.InstrumentId == instrument.InstrumentId && x.Selection.StrategyId == strategy
                    && x.Selection.IntervalSeconds == c.Profile.IntervalSeconds);
                if (f?.PriceFeaturesReady != true || f.FeatureVersion != c.Feature || signal?.Selection.Enabled != true
                    || now - f.TimestampUtc > TimeSpan.FromSeconds(c.Profile.IntervalSeconds + 3)
                    || f.SampleCount < signal.Selection.MinimumBars || signal.ApprovedFeatureVersion != c.Feature
                    || signal.ApprovedSignalVersion != signal.Version || c.Pattern != signal.ApprovedPatternVersion + "+" + signal.Version)
                { shadowReady = false; reasons.Add($"{strategy}: current short quote features, warmup and explicit signal approval are required."); }
                var identities = compatible.Select(x => $"{ForexResearchContract.Model}:{x.Id}:{x.ModelVersion}").ToHashSet();
                var observations = raw.Where(x => x.StrategyId == strategy).Select(x => (Record: x, Proof: ForexShadowService.ReadProof(x)))
                    .Where(x => x.Proof != null && x.Proof.Feature == c.Feature && x.Proof.Pattern == c.Pattern && x.Proof.Label == c.Label
                        && x.Proof.Mode == "AnalysisOnly" && (!x.Record.Approved || identities.Contains(x.Proof.ModelIdentity)))
                    .GroupBy(x => x.Record.SignalId).Select(x => x.First()).ToArray();
                var accepted = observations.Count(x => x.Record.Approved);
                count += observations.Length; approved += accepted;
                var latency = observations.Select(x => x.Proof!.ElapsedMilliseconds).Order().ToArray();
                var p95 = latency.Length > 0 ? latency[(int)Math.Ceiling(latency.Length * .95) - 1] : double.PositiveInfinity;
                var span = observations.Length > 1 ? (observations.Max(x => x.Record.EvaluatedAtUtc) - observations.Min(x => x.Record.EvaluatedAtUtc)).TotalMinutes : 0;
                var p95Signal = P95(observations.Select(x => x.Proof!.SignalAgeMilliseconds));
                var p95Quote = P95(observations.Select(x => x.Proof!.QuoteAgeMilliseconds));
                var net = accepted == 0 ? 0 : observations.Where(x => x.Record.Approved).Average(x => x.Record.ExpectedNetEdgeBps);
                if (observations.Length < Math.Max(s.MinimumShadowDecisions, performance.Value.MinimumDecisionsPerStrategy)
                    || accepted < s.MinimumApprovedShadowDecisions || span < performance.Value.MinimumObservationMinutes
                    || !double.IsFinite(p95) || p95 > performance.Value.MaximumP95SignalProcessingMilliseconds
                    || p95Signal > performance.Value.MaximumP95SignalAgeMilliseconds || p95Quote > performance.Value.MaximumP95QuoteAgeMilliseconds
                    || net < performance.Value.MinimumAverageExpectedNetEdgeBps)
                    sampleReady = false;
                reasons.Add($"{strategy}: AnalysisOnly observations={observations.Length}, passed={accepted}, span={span:F1} min; model/signal-age/quote-age p95={p95:F2}/{p95Signal:F2}/{p95Quote:F2} ms; proposed net={net:F2} bps. Thresholds are evaluated per strategy.");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { researchReady = shadowReady = sampleReady = false; reasons.Add($"{strategy}: FX research configuration or evidence is incomplete ({ex.GetType().Name})."); }
        }
        shadowReady &= researchReady && critical.Count == 0;
        var passed = shadowReady && sampleReady;
        if (instrument.Status is InstrumentOnboardingStatus.PaperReady or InstrumentOnboardingStatus.LiveEnabled)
        {
            if (!shadowReady) critical.Add("FX research/model/feature readiness has been lost.");
            var e = engine.Current;
            if (!e.ReconciliationCompleted || e.Mismatches.Count > 0 || e.State is TradingEngineState.Degraded or TradingEngineState.Faulted)
                critical.Add("Broker reconciliation is not healthy.");
        }
        reasons.AddRange(critical);
        reasons.Add($"Readiness: history={historyReady}; research={researchReady}; model shadow={shadowReady}; measured sample={passed}.");
        reasons.Add("Model shadow is not a risk approval. FX-9 rechecks account, broker rules, costs, reservations and paper permissions. Actual IBKR fills remain unverified (FX-12).");
        var target = instrument.Status;
        if (s.Enabled && apply && instrument.ConfiguredEnabled)
        {
            if (s.AutoSuspend && critical.Count > 0 && instrument.Status is InstrumentOnboardingStatus.PaperReady or InstrumentOnboardingStatus.LiveEnabled) target = InstrumentOnboardingStatus.Suspended;
            else if (instrument.Status == InstrumentOnboardingStatus.Backfilling && historyComplete) target = InstrumentOnboardingStatus.Collecting;
            else if (instrument.Status == InstrumentOnboardingStatus.Collecting && historyReady) target = InstrumentOnboardingStatus.WarmingUp;
            else if (instrument.Status == InstrumentOnboardingStatus.WarmingUp && researchReady) target = InstrumentOnboardingStatus.ResearchReady;
            else if (instrument.Status == InstrumentOnboardingStatus.ResearchReady && shadowReady && s.AutoAdvanceToShadow) target = InstrumentOnboardingStatus.ShadowReady;
            else if (instrument.Status == InstrumentOnboardingStatus.ShadowReady && !shadowReady) target = InstrumentOnboardingStatus.ResearchReady;
            else if (instrument.Status == InstrumentOnboardingStatus.ShadowReady && passed && s.AutoAdvanceToPaper) target = InstrumentOnboardingStatus.PaperReady;
            if (target != instrument.Status) await registry.TransitionAsync(instrument.InstrumentId, instrument.Status, target, string.Join(" ", reasons), "FxRolloutMonitor", ct);
        }
        var record = new InstrumentRolloutEvaluationRecord { InstrumentId = instrument.InstrumentId, Symbol = instrument.Symbol,
            StatusBefore = instrument.Status, StatusAfter = target, EvaluatedAtUtc = now, WindowStartUtc = shadowFrom,
            ShadowDecisionCount = count, ApprovedShadowDecisionCount = approved, ShadowCriteriaPassed = passed,
            QualityIncidentCount = incidents, UnhealthyStreamCount = streams.Count(x => !x.IsHealthy), UnknownOrderCount = unknownOrders,
            Suspended = target == InstrumentOnboardingStatus.Suspended && target != instrument.Status,
            ReasonsJson = JsonSerializer.Serialize(reasons) };
        db.InstrumentRolloutEvaluationRecords.Add(record); await db.SaveChangesAsync(ct);
        return new() { Id = record.Id, InstrumentId = instrument.InstrumentId, Symbol = instrument.Symbol, StatusBefore = instrument.Status, StatusAfter = target,
            EvaluatedAtUtc = now, WindowStartUtc = shadowFrom, ShadowDecisionCount = count, ApprovedShadowDecisionCount = approved,
            ShadowCriteriaPassed = passed, Suspended = record.Suspended, QualityIncidentCount = incidents, UnhealthyStreamCount = record.UnhealthyStreamCount, UnknownOrderCount = unknownOrders, Reasons = reasons };
    }

    private static double P95(IEnumerable<double?> source)
    {
        var values = source.ToArray();
        if (values.Length == 0 || values.Any(x => x == null || !double.IsFinite(x.Value) || x < 0)) return double.PositiveInfinity;
        return values.Select(x => x!.Value).Order().ElementAt((int)Math.Ceiling(values.Length * .95) - 1);
    }
}
