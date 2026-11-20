using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Services;

/// <summary>
/// Conservative observation coverage, not proof of executable liquidity or network health.
/// Missing updates alone cannot distinguish a quiet market from lost data.
/// </summary>
public sealed class ForexReplayCoveragePolicy(int maximumQuoteGapSeconds, int maximumSnapshotAgeSeconds,
    ForexReplayCoverageEvidence? evidence = null)
{
    public const string Version = "fx-quote-coverage-v1";

    public static ForexReplayCoverageEvidence? FreezeEvidence(ForexReplayCoverageEvidence? value,
        string instrumentId, DateTime from, DateTime to)
    {
        if (value == null) return null;
        if (value.InstrumentId != instrumentId || string.IsNullOrWhiteSpace(value.Source)
            || value.FromUtc.Kind != DateTimeKind.Utc || value.ToUtc.Kind != DateTimeKind.Utc
            || value.FromUtc >= value.ToUtc || value.FromUtc > from || value.ToUtc < to
            || value.Interruptions == null || value.Interruptions.Any(x => x == null
                || x.StartUtc.Kind != DateTimeKind.Utc || x.EndUtc.Kind != DateTimeKind.Utc
                || x.StartUtc > x.EndUtc || x.StartUtc < value.FromUtc || x.EndUtc > value.ToUtc
                || string.IsNullOrWhiteSpace(x.Reason)))
            throw new InvalidDataException("FX coverage evidence must match the instrument, cover the replay range and contain bounded UTC interruptions with a source.");
        return value with { Interruptions = value.Interruptions.Distinct().OrderBy(x => x.StartUtc)
            .ThenBy(x => x.EndUtc).ThenBy(x => x.Reason, StringComparer.Ordinal).ToArray() };
    }

    public ForexReplayCoverageIssue Issue(string reason, DateTime at, ForexReplayQuote? previous = null,
        ForexReplayQuote? current = null, ForexReplayInterruption? interruption = null) =>
        new(Version, reason, at, Observation(previous), Observation(current), maximumQuoteGapSeconds,
            maximumSnapshotAgeSeconds, interruption);

    public ForexReplayCoverageIssue? AtDecision(ForexReplayQuote? quote, DateTime at)
    {
        if (quote == null) return Issue("NoDecisionQuote", at);
        var interruption = FindInterruption(quote.ReceivedUtc, at);
        if (interruption != null) return Issue("RecordedInterruption", at, quote, interruption: interruption);
        if (quote.AvailableUtc > at || at - quote.EventUtc > TimeSpan.FromSeconds(maximumSnapshotAgeSeconds))
            return Issue("StaleDecisionQuote", at, quote);
        if (!MarketDataSession.IsForexOpen(at)) return Issue("MarketSessionClosed", at, quote);
        return null;
    }

    public ForexReplayCoverageIssue? Between(ForexReplayQuote previous, ForexReplayQuote current)
    {
        var at = current.AvailableUtc;
        var interruption = FindInterruption(previous.ReceivedUtc, at);
        if (interruption != null) return Issue("RecordedInterruption", at, previous, current, interruption);
        if (Session(previous.Id) != Session(current.Id)) return Issue("CallbackSessionChanged", at, previous, current);
        if (at - current.EventUtc > TimeSpan.FromSeconds(maximumSnapshotAgeSeconds))
            return Issue("StaleReceivedQuote", at, previous, current);
        if (!MarketDataSession.IsForexOpen(current.EventUtc)) return Issue("MarketSessionClosed", at, previous, current);
        if (at - previous.AvailableUtc > TimeSpan.FromSeconds(maximumQuoteGapSeconds))
            return Issue("UnobservedQuoteInterval", at, previous, current);
        return null;
    }

    public ForexReplayCoverageIssue? AfterLastObservation(ForexReplayQuote previous, DateTime requiredUntilUtc)
    {
        var interruption = FindInterruption(previous.ReceivedUtc, requiredUntilUtc);
        return interruption == null ? null
            : Issue("RecordedInterruption", requiredUntilUtc, previous, interruption: interruption);
    }

    private ForexReplayInterruption? FindInterruption(DateTime from, DateTime to) =>
        evidence?.Interruptions.FirstOrDefault(x => x.StartUtc <= to && x.EndUtc >= from);

    private static string Session(string id) => id[..id.LastIndexOf(':')];
    private static ForexReplayQuoteObservation? Observation(ForexReplayQuote? q) =>
        q == null ? null : new(q.Id, q.EventUtc, q.ReceivedUtc, q.AvailableUtc);
}
