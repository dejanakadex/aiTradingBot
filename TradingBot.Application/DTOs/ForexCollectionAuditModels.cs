namespace TradingBot.Application.DTOs;

public sealed record ForexCollectionAuditReport(
    string InstrumentId, DateTime FromUtc, DateTime ToUtc, string AggregateVersion,
    int QuoteEvents, int CompleteCallbackPairs, int IncompleteCallbackPairs, int CallbackSessions,
    DateTime? FirstEventUtc, DateTime? LastEventUtc, double? MinimumReceiveMinusEventMilliseconds,
    double? MedianReceiveMinusEventMilliseconds, double? P95ReceiveMinusEventMilliseconds,
    int FutureQuoteEvents, int StaleQuoteEvents, ForexAggregateDiagnostics Diagnostics,
    IReadOnlyList<ForexIntervalAudit> Intervals);

public sealed record ForexIntervalAudit(
    int IntervalSeconds, int ExpectedFinalWindows, int WindowsWithoutQuotes,
    int ObservedWindowsWithoutFinalBar, int MissingBarsWithFutureQuotes, int MissingBarsWithStaleQuotes,
    int FinalBars, int MaximumConsecutiveFeatureBars, int FramesWithAtLeast26Bars,
    int PriceFeatureReadyFrames, int LongestEmptyRun, IReadOnlyDictionary<int, int> ConsecutiveBarHistogram);
