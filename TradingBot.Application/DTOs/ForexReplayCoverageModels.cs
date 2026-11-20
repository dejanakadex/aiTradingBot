namespace TradingBot.Application.DTOs;

// Frozen, explicitly supplied evidence. An empty list does not prove uninterrupted connectivity.
public sealed record ForexReplayCoverageEvidence(string InstrumentId, DateTime FromUtc, DateTime ToUtc,
    string Source, ForexReplayInterruption[] Interruptions);

public sealed record ForexReplayInterruption(DateTime StartUtc, DateTime EndUtc, string Reason);

public sealed record ForexReplayQuoteObservation(string Id, DateTime EventUtc, DateTime ReceivedUtc, DateTime AvailableUtc);

public sealed record ForexReplayCoverageIssue(string PolicyVersion, string Reason, DateTime CheckedAtUtc,
    ForexReplayQuoteObservation? PreviousQuote, ForexReplayQuoteObservation? CurrentQuote,
    int MaximumQuoteGapSeconds, int MaximumSnapshotAgeSeconds, ForexReplayInterruption? RecordedInterruption = null);
