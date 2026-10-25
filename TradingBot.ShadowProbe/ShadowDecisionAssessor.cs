using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.ShadowProbe;

public static class ShadowDecisionAssessor
{
    public static async Task<ShadowDecisionReport> AssessAsync(string databasePath, DateTime startUtc,
        DateTime endUtc, ShadowPerformanceSettings thresholds, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(databasePath);
        if (!File.Exists(path)) throw new FileNotFoundException("Shadow operational database is unavailable.", path);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        var options = new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connectionString).Options;
        await using var db = new TradingBotDbContext(options);

        var decisions = await db.ScalpingExecutionDecisionRecords.AsNoTracking()
            .Where(x => x.EvaluatedAtUtc >= startUtc && x.EvaluatedAtUtc <= endUtc
                && x.SelectedPolicy != TradingBot.Infrastructure.Services.ForexShadowService.Policy)
            .OrderBy(x => x.EvaluatedAtUtc).Take(100001).ToArrayAsync(cancellationToken);
        var candidates = await db.ResearchCandidateRecords.AsNoTracking()
            .Where(x => x.ReplayRunId == null && x.CreatedAtUtc >= startUtc && x.CreatedAtUtc <= endUtc)
            .OrderBy(x => x.CreatedAtUtc).Take(100001).ToArrayAsync(cancellationToken);
        var timingRows = await db.AiAnalysisRecords.AsNoTracking()
            .Where(x => x.Context == "ShadowPatternTiming" && x.TimestampUtc >= startUtc && x.TimestampUtc <= endUtc)
            .OrderBy(x => x.TimestampUtc).Select(x => x.ResultJson).Take(100001).ToArrayAsync(cancellationToken);
        var riskRows = await db.RiskDecisionRecords.AsNoTracking()
            .Where(x => x.TimestampUtc >= startUtc && x.TimestampUtc <= endUtc)
            .Select(x => x.DecisionJson).Take(100001).ToArrayAsync(cancellationToken);
        var models = await db.NumericalModelRecords.AsNoTracking()
            .Where(x => x.Status == NumericalModelStatus.Approved)
            .Select(x => new { x.InstrumentId, x.StrategyId, x.DecidedAtUtc, x.BaselineHoldoutJson, x.CandidateHoldoutJson })
            .ToArrayAsync(cancellationToken);
        var qualityCount = await db.MarketDataQualityIncidentRecords.AsNoTracking()
            .CountAsync(x => x.RecordedAtUtc >= startUtc && x.RecordedAtUtc <= endUtc, cancellationToken);
        var gaps = await db.TickCoverageGapRecords.AsNoTracking()
            .Where(x => x.EndUtc >= startUtc && x.StartUtc <= endUtc)
            .ToArrayAsync(cancellationToken);
        var orderCount = await db.OrderRecords.AsNoTracking()
            .CountAsync(x => x.CreatedUtc >= startUtc && x.CreatedUtc <= endUtc, cancellationToken);

        var timings = timingRows.Select(ParseTiming).Where(x => x != null).Cast<Timing>().ToArray();
        var riskApprovals = riskRows.Select(ParseRiskApproval).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        var groups = decisions.Select(x => (x.InstrumentId, x.StrategyId))
            .Concat(candidates.Select(x => (x.InstrumentId, x.StrategyId)))
            .Concat(timings.Select(x => (x.InstrumentId, x.StrategyId)))
            .Distinct().OrderBy(x => x.InstrumentId).ThenBy(x => x.StrategyId).ToArray();
        var results = new List<ShadowStrategyResult>();
        foreach (var (instrumentId, strategyId) in groups)
        {
            var scopedDecisions = decisions.Where(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId).ToArray();
            var scopedCandidates = candidates.Where(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId).ToArray();
            var scopedTimings = timings.Where(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId).ToArray();
            var timedSignalIds = scopedTimings.Select(x => x.SignalId).ToHashSet();
            var approved = scopedDecisions.Where(x => x.Approved).ToArray();
            var model = models.Where(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId)
                .OrderByDescending(x => x.DecidedAtUtc).FirstOrDefault();
            var baseline = ReadMetrics(model?.BaselineHoldoutJson);
            var candidate = ReadMetrics(model?.CandidateHoldoutJson);
            var p95Age = Percentile(scopedTimings.Select(x => x.SignalAgeMilliseconds));
            var p95Processing = Percentile(scopedTimings.Select(x => x.ProcessingMilliseconds));
            var p95Quote = Percentile(scopedDecisions.Where(x => x.QuoteAsOfUtc.HasValue)
                .Select(x => Math.Max(0, (x.EvaluatedAtUtc - x.QuoteAsOfUtc!.Value).TotalMilliseconds)));
            var meanNet = approved.Length == 0 ? (decimal?)null : approved.Average(x => x.ExpectedNetEdgeBps);
            var missingRisk = approved.Count(x => !riskApprovals.Contains(x.SignalId));
            var rejectionStages = scopedCandidates.Where(x => x.Outcome != ResearchCandidateOutcome.Accepted)
                .GroupBy(x => string.IsNullOrWhiteSpace(x.DecisionStage) ? "Unknown" : x.DecisionStage)
                .ToDictionary(x => x.Key, x => x.Count());
            var reasons = new List<string>();
            if (scopedDecisions.Length < thresholds.MinimumDecisionsPerStrategy)
                reasons.Add($"Execution decisions {scopedDecisions.Length}/{thresholds.MinimumDecisionsPerStrategy}.");
            if (approved.Length == 0) reasons.Add("No approved shadow decision reached the final execution gate.");
            var decisionsWithoutTiming = scopedDecisions.Count(x => !timedSignalIds.Contains(x.SignalId));
            if (decisionsWithoutTiming > 0)
                reasons.Add($"{decisionsWithoutTiming} execution decision(s) lack timing for the same signal.");
            if (rejectionStages.GetValueOrDefault("PatternQueue") > 0)
                reasons.Add("One or more pattern candidates could not enter the decision queue.");
            if (p95Age is null || p95Age > thresholds.MaximumP95SignalAgeMilliseconds)
                reasons.Add("P95 signal age is missing or above threshold.");
            if (p95Processing is null || p95Processing > thresholds.MaximumP95SignalProcessingMilliseconds)
                reasons.Add("P95 signal processing time is missing or above threshold.");
            if (p95Quote is null || p95Quote > thresholds.MaximumP95QuoteAgeMilliseconds)
                reasons.Add("P95 quote age is missing or above threshold.");
            var peakQueueDepth = scopedTimings.Select(x => x.PendingPatternQueueDepth).DefaultIfEmpty().Max();
            if (peakQueueDepth > thresholds.MaximumPendingPatternQueueDepth)
                reasons.Add("Pending pattern queue depth exceeded threshold.");
            if (meanNet is null || meanNet < thresholds.MinimumAverageExpectedNetEdgeBps)
                reasons.Add("Average expected net edge is missing or below threshold.");
            if (baseline is null || candidate is null || candidate.ExpectancyBps <= baseline.ExpectancyBps)
                reasons.Add("Approved model holdout expectancy does not beat the deterministic baseline or is unavailable.");
            if (missingRisk > 0) reasons.Add($"{missingRisk} approved decision(s) lack an approved risk audit for the same signal.");
            results.Add(new ShadowStrategyResult(instrumentId, strategyId, scopedDecisions.Length, approved.Length,
                scopedDecisions.Length - approved.Length, scopedTimings.Length, p95Age, p95Processing,
                p95Quote, peakQueueDepth, meanNet,
                candidate?.ExpectancyBps, baseline?.ExpectancyBps, rejectionStages, missingRisk, reasons.Count == 0, reasons));
        }

        var globalReasons = new List<string>();
        if ((endUtc - startUtc).TotalMinutes < thresholds.MinimumObservationMinutes)
            globalReasons.Add($"Observation shorter than {thresholds.MinimumObservationMinutes} minutes.");
        if (groups.Length == 0) globalReasons.Add("No live shadow decisions or signal timings were recorded.");
        if (decisions.Length > 100000 || candidates.Length > 100000 || timingRows.Length > 100000 || riskRows.Length > 100000)
            globalReasons.Add("Assessment row limit reached; narrow the window.");
        if (qualityCount > thresholds.MaximumRecentQualityIncidents)
            globalReasons.Add($"Market-data quality incidents: {qualityCount}.");
        if (gaps.Length > 0) globalReasons.Add($"Tick coverage gaps: {gaps.Length}.");
        if (gaps.Sum(x => x.DroppedEvents) > 0) globalReasons.Add("Tick events were dropped.");
        if (orderCount > 0) globalReasons.Add($"Broker order records appeared during shadow-only observation: {orderCount}.");
        if (results.Any(x => !x.Passed)) globalReasons.Add("One or more strategy assessments did not meet thresholds.");
        return new ShadowDecisionReport(startUtc, endUtc, qualityCount, gaps.Length,
            gaps.Sum(x => x.DroppedEvents), orderCount, results, globalReasons.Count == 0, globalReasons);
    }

    private static double? Percentile(IEnumerable<double> values) =>
        ShadowFeedAccumulator.Percentile(values.Order().ToArray(), 0.95);

    private static NumericalModelMetrics? ReadMetrics(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<NumericalModelMetrics>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }

    private static Timing? ParseTiming(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Timing(root.GetProperty("SignalId").GetGuid(),
                root.GetProperty("InstrumentId").GetString() ?? "",
                root.GetProperty("StrategyId").GetString() ?? "",
                root.GetProperty("signalAgeMilliseconds").GetDouble(),
                root.GetProperty("processingMilliseconds").GetDouble(),
                root.GetProperty("pendingPatternQueueDepth").GetInt32());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static Guid? ParseRiskApproval(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var risk = doc.RootElement.GetProperty("riskDecision");
            if (risk.GetProperty("decision").GetInt32() != (int)RiskDecisionType.Approve
                || risk.GetProperty("reservationId").ValueKind != JsonValueKind.String) return null;
            return risk.GetProperty("context").GetProperty("signalId").GetGuid();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }

    private sealed record Timing(Guid SignalId, string InstrumentId, string StrategyId, double SignalAgeMilliseconds,
        double ProcessingMilliseconds, int PendingPatternQueueDepth);
}
