using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

/// <summary>Predeclared research boundaries. This is not model or execution approval.</summary>
public sealed record ForexDatasetPlan(string InstrumentId, string StrategyId, DateTime ReservedUtc,
    DateTime TrainingFromUtc, DateTime ValidationFromUtc, DateTime HoldoutFromUtc, DateTime HoldoutToUtc,
    int BoundaryBufferSeconds)
{
    public void Validate(ResearchEvaluationSettings evaluation, ForexResearchProfile profile)
    {
        if (new[] { ReservedUtc, TrainingFromUtc, ValidationFromUtc, HoldoutFromUtc, HoldoutToUtc }.Any(x => x.Kind != DateTimeKind.Utc)
            || ReservedUtc > TrainingFromUtc || TrainingFromUtc >= ValidationFromUtc || ValidationFromUtc >= HoldoutFromUtc
            || HoldoutFromUtc >= HoldoutToUtc || evaluation.GetValidationErrors().Count != 0
            || ValidationFromUtc - TrainingFromUtc < TimeSpan.FromDays(evaluation.TrainingWindowDays)
            || HoldoutFromUtc - ValidationFromUtc < TimeSpan.FromDays(evaluation.ValidationWindowDays)
            || HoldoutToUtc - HoldoutFromUtc < TimeSpan.FromDays(evaluation.HoldoutDays)
            || HoldoutToUtc - TrainingFromUtc > TimeSpan.FromDays(366)
            || InstrumentId != profile.InstrumentId || StrategyId != profile.StrategyId
            || BoundaryBufferSeconds < (ForexTradeSimulator.WindowEnd(TrainingFromUtc, profile) - TrainingFromUtc).TotalSeconds
            || BoundaryBufferSeconds > 3600)
            throw new ArgumentException("Invalid UTC dataset plan, evaluation windows or execution-horizon buffer.");
    }

    // Use the actual label/exit end, not just a nominal horizon. Unknown exits never enter a split.
    public bool ContainsLabel(string partition, DateTime decisionUtc, DateTime? labelEndUtc)
    {
        var (from, to) = partition switch
        {
            "training" => (TrainingFromUtc, ValidationFromUtc),
            "validation" => (ValidationFromUtc, HoldoutFromUtc),
            "holdout" => (HoldoutFromUtc, HoldoutToUtc),
            _ => throw new ArgumentException("Unknown partition.", nameof(partition))
        };
        return decisionUtc.Kind == DateTimeKind.Utc && labelEndUtc?.Kind == DateTimeKind.Utc
            && decisionUtc >= from.AddSeconds(BoundaryBufferSeconds)
            && decisionUtc < to.AddSeconds(-BoundaryBufferSeconds)
            && labelEndUtc >= decisionUtc && labelEndUtc <= to;
    }
}

public sealed class ForexDatasetDayReview
{
    public DateTime DayUtc { get; init; }
    public long Pairs { get; set; }
    public long StalePairs { get; set; }
    public long RulesUnavailablePairs { get; set; }
    public long GapsOverMaximum { get; set; }
    public double MaximumGapSeconds { get; set; }
    public decimal SpreadSumPips { get; set; }
    public decimal MaximumSpreadPips { get; set; }
    public decimal MeanSpreadPips => Pairs == 0 ? 0 : SpreadSumPips / Pairs;
    public long[] SpreadBuckets { get; } = new long[5];
    public DateTime? FirstQuoteUtc { get; set; }
    public DateTime? LastQuoteUtc { get; set; }
    public int ObservedMinutes { get; set; }
    public decimal MeanObservedMinuteRangePips { get; set; }
}

/// <summary>Bounded receive-ordered quality review. Reuses the replay reader; never labels, trains or connects to a broker.</summary>
public static class ForexDatasetReview
{
    public static async Task<object> AnalyzeAsync(IMarketDatasetStore source, TradingSettings trading,
        ShortAggregateSettings aggregates, ForexResearchProfile profile, ForexRiskProfile costs,
        ForexDatasetPlan plan, ResearchEvaluationSettings evaluation, DateTime from, DateTime to,
        CancellationToken token = default)
    {
        plan.Validate(evaluation, profile);
        if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from >= to || to - from > TimeSpan.FromDays(31)
            || to > plan.HoldoutFromUtc)
            throw new ArgumentException("Review requires a positive UTC range of at most 31 days, entirely before reserved holdout.");
        if (costs.PipSize <= 0 || costs.InstrumentId != profile.InstrumentId || costs.StrategyId != profile.StrategyId)
            throw new ArgumentException("Matching FX cost profile required.");
        var instrument = trading.GetConfiguredInstruments().Single(x => x.InstrumentId == profile.InstrumentId);
        var manifest = await source.GetManifestAsync(token);
        if (manifest.Files.Any(x => x.InstrumentId == profile.InstrumentId && x.DataType is "bid" or "ask"
            && x.MaximumEventTimeUtc >= plan.HoldoutFromUtc))
            throw new InvalidDataException("Snapshot includes reserved holdout files; use a separate development snapshot.");
        var before = await source.VerifyAsync(token);
        if (!before.IsValid) throw new InvalidDataException("Frozen dataset verification failed.");
        using var index = new ForexReplayIndex();
        await index.LoadAsync(source, new(profile.InstrumentId, from, to), instrument.Symbol, aggregates, token);
        var days = new SortedDictionary<DateTime, ForexDatasetDayReview>();
        var minutes = new Dictionary<DateTime, (decimal Low, decimal High)>();
        ForexReplayQuote? previous = null;
        foreach (var q in index.Quotes(from, to, token))
        {
            if (q.AvailableUtc < from || q.AvailableUtc >= to) continue;
            var day = q.AvailableUtc.Date;
            if (!days.TryGetValue(day, out var d)) days.Add(day, d = new() { DayUtc = day });
            d.Pairs++; d.FirstQuoteUtc ??= q.AvailableUtc; d.LastQuoteUtc = q.AvailableUtc;
            if ((q.AvailableUtc - q.EventUtc).TotalSeconds > aggregates.ForexMaximumQuoteAgeSeconds) d.StalePairs++;
            var rule = profile.HistoricalRules.Where(x => x.ObservedUtc <= q.AvailableUtc && x.Rules.VerifiedAtUtc <= q.AvailableUtc)
                .OrderByDescending(x => x.ObservedUtc).FirstOrDefault();
            if (rule == null || (q.AvailableUtc - rule.Rules.VerifiedAtUtc).TotalHours > costs.MaximumRulesAgeHours) d.RulesUnavailablePairs++;
            if (previous != null)
            {
                var gap = (q.AvailableUtc - previous.AvailableUtc).TotalSeconds;
                d.MaximumGapSeconds = Math.Max(d.MaximumGapSeconds, gap);
                if (gap > profile.MaximumQuoteGapSeconds) d.GapsOverMaximum++;
            }
            previous = q;
            var spread = (q.Ask - q.Bid) / costs.PipSize;
            d.SpreadSumPips += spread; d.MaximumSpreadPips = Math.Max(d.MaximumSpreadPips, spread);
            d.SpreadBuckets[spread <= .5m ? 0 : spread <= 1 ? 1 : spread <= 2 ? 2 : spread <= 5 ? 3 : 4]++;
            var at = new DateTime(q.AvailableUtc.Ticks - q.AvailableUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var mid = (q.Bid + q.Ask) / 2;
            minutes[at] = minutes.TryGetValue(at, out var m) ? (Math.Min(m.Low, mid), Math.Max(m.High, mid)) : (mid, mid);
        }
        foreach (var d in days.Values)
        {
            var ranges = minutes.Where(x => x.Key.Date == d.DayUtc).Select(x => (x.Value.High - x.Value.Low) / costs.PipSize).ToArray();
            d.ObservedMinutes = ranges.Length;
            d.MeanObservedMinuteRangePips = ranges.Length == 0 ? 0 : ranges.Average();
        }
        var after = await source.VerifyAsync(token);
        if (!after.IsValid || before.ManifestSha256 != after.ManifestSha256) throw new InvalidDataException("Dataset changed during review.");
        return new
        {
            Version = "fx-dataset-review-v1", CanSendOrders = false, N5Complete = false,
            FromUtc = from, ToUtc = to, before.ManifestSha256, index.CanonicalSha256, index.EventCount, index.DuplicateEvents,
            Days = days.Values, Plan = plan,
            SpreadBucketUpperBoundsPips = new decimal?[] { .5m, 1, 2, 5, null },
            Limitations = new[] {
                "Integrity and quote quality only; no labels, strategy returns, training or holdout prices read.",
                "Observed minutes and quote-weighted spread are not continuous session coverage or feature-ready bars.",
                "Minute high-low is an observed range proxy, not an unbiased volatility estimate.",
                "Gaps include closed sessions and planned interruptions; they are not automatically broker data loss.",
                "Rules availability counts check observation/expiry only, not all execution risk or account conditions.",
                "Review does not certify N5: multiple representative sessions, rules, exclusions and untouched final data still require acceptance.",
                "Plan label membership must be applied to actual completed labels in subsequent research; other CLI commands do not enforce this reservation." }
        };
    }
}
