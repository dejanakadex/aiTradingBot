using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.ShadowProbe;

public sealed record ShadowFeedThresholds(
    int MinimumDurationSeconds = 1800,
    int MinimumTradeTicksPerInstrument = 1000,
    int MinimumBidAskTicksPerInstrument = 1000,
    double MaximumP95ReceiveLagMilliseconds = 1500,
    int MaximumDroppedEvents = 0);

public sealed record ShadowInstrumentResult(
    string InstrumentId,
    string Symbol,
    long TradeTicks,
    long BidTicks,
    long AskTicks,
    long DroppedEvents,
    long OutOfOrderEvents,
    int PeakQueueDepth,
    double? P50ReceiveLagMilliseconds,
    double? P95ReceiveLagMilliseconds,
    double? P99ReceiveLagMilliseconds,
    int? BrokerMarketDataErrorCode,
    bool Passed,
    IReadOnlyList<string> Reasons);

public sealed record ShadowFeedReport(
    DateTime StartedAtUtc,
    DateTime EndedAtUtc,
    int DurationSeconds,
    int? ServerVersion,
    ShadowFeedThresholds Thresholds,
    IReadOnlyList<ShadowInstrumentResult> Instruments,
    bool Passed,
    IReadOnlyList<string> Reasons)
{
    public ShadowDecisionReport? DecisionAssessment { get; init; }
}

public sealed record ShadowStrategyResult(
    string InstrumentId,
    string StrategyId,
    int DecisionCount,
    int ApprovedCount,
    int RejectedCount,
    int PatternTimingCount,
    double? P95SignalAgeMilliseconds,
    double? P95ProcessingMilliseconds,
    double? P95QuoteAgeMilliseconds,
    int PeakPendingPatternQueueDepth,
    decimal? AverageExpectedNetEdgeBps,
    decimal? ModelHoldoutExpectancyBps,
    decimal? BaselineHoldoutExpectancyBps,
    IReadOnlyDictionary<string, int> RejectionStages,
    int ApprovedDecisionsWithoutRiskApproval,
    bool Passed,
    IReadOnlyList<string> Reasons);

public sealed record ShadowDecisionReport(
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    int QualityIncidentCount,
    int TickCoverageGapCount,
    long DroppedTickCount,
    int BrokerOrderCount,
    IReadOnlyList<ShadowStrategyResult> Strategies,
    bool Passed,
    IReadOnlyList<string> Reasons);

public sealed class ShadowFeedAccumulator(string instrumentId, string symbol)
{
    private readonly List<double> _lags = [];
    private readonly Dictionary<MarketDataEventKind, DateTime> _lastEventTime = [];
    private readonly object _sync = new();
    private long _trades;
    private long _bids;
    private long _asks;
    private long _outOfOrder;
    private int _peakQueueDepth;

    public void Add(CanonicalMarketDataEvent item, int queueDepth)
    {
        if (item.InstrumentId != instrumentId || item.Symbol != symbol)
            throw new ArgumentException("Tick belongs to another instrument.", nameof(item));
        lock (_sync)
        {
            switch (item.Kind)
            {
                case MarketDataEventKind.Trade: _trades++; break;
                case MarketDataEventKind.Bid: _bids++; break;
                case MarketDataEventKind.Ask: _asks++; break;
            }
            if (_lastEventTime.TryGetValue(item.Kind, out var previous) && item.EventTimeUtc < previous)
                _outOfOrder++;
            if (!_lastEventTime.TryGetValue(item.Kind, out previous) || item.EventTimeUtc > previous)
                _lastEventTime[item.Kind] = item.EventTimeUtc;
            _peakQueueDepth = Math.Max(_peakQueueDepth, Math.Max(0, queueDepth));
            _lags.Add(Math.Max(0, (item.ReceivedTimeUtc - item.EventTimeUtc).TotalMilliseconds));
        }
    }

    public ShadowInstrumentResult Finish(long droppedEvents, int? brokerError, ShadowFeedThresholds thresholds)
    {
        lock (_sync)
        {
            var sorted = _lags.Order().ToArray();
            var p95 = Percentile(sorted, 0.95);
            var reasons = new List<string>();
            if (_trades < thresholds.MinimumTradeTicksPerInstrument)
                reasons.Add($"Trade ticks {_trades}/{thresholds.MinimumTradeTicksPerInstrument}.");
            if (_bids < thresholds.MinimumBidAskTicksPerInstrument || _asks < thresholds.MinimumBidAskTicksPerInstrument)
                reasons.Add($"Bid/ask ticks {_bids}/{_asks}; minimum {thresholds.MinimumBidAskTicksPerInstrument} each.");
            if (droppedEvents > thresholds.MaximumDroppedEvents)
                reasons.Add($"Tick queue dropped {droppedEvents} event(s).");
            if (_outOfOrder > 0) reasons.Add($"Observed {_outOfOrder} out-of-order event(s).");
            if (p95 is null || p95 > thresholds.MaximumP95ReceiveLagMilliseconds)
                reasons.Add($"P95 receive lag {p95?.ToString("F0") ?? "unavailable"} ms exceeds {thresholds.MaximumP95ReceiveLagMilliseconds:F0} ms.");
            if (brokerError.HasValue) reasons.Add($"Broker market-data error code {brokerError.Value}.");
            return new ShadowInstrumentResult(instrumentId, symbol, _trades, _bids, _asks,
                droppedEvents, _outOfOrder, _peakQueueDepth, Percentile(sorted, 0.5), p95,
                Percentile(sorted, 0.99), brokerError, reasons.Count == 0, reasons);
        }
    }

    public static double? Percentile(IReadOnlyList<double> sortedValues, double quantile)
    {
        if (sortedValues.Count == 0) return null;
        if (quantile is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(quantile));
        var index = (int)Math.Ceiling(quantile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
    }
}
