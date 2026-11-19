using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Services;

/// <summary>One collector's bounded minute accumulator. No quote prices, accounts or event buffering.</summary>
internal sealed class TickLatencyWindow
{
    private long _count, _failed, _overTwoSeconds, _negativeQueue;
    private double _ageSum, _queueSum, _processingSum, _maxAge = double.NegativeInfinity;
    private double _maxQueue = double.NegativeInfinity, _maxProcessing;
    private DateTime? _worstEvent, _worstReceived;
    private double _worstQueue, _worstProcessing;

    public void Record(CanonicalMarketDataEvent item, DateTime dequeuedUtc, TimeSpan processing, bool failed)
    {
        var age = (item.ReceivedTimeUtc - item.EventTimeUtc).TotalMilliseconds;
        var queue = (dequeuedUtc - item.ReceivedTimeUtc).TotalMilliseconds;
        var elapsed = processing.TotalMilliseconds;
        _count++; if (failed) _failed++;
        if (age > 2000) _overTwoSeconds++;
        if (queue < 0) _negativeQueue++; // Wall-clock corrections remain visible, never clamped into latency.
        _ageSum += age; _queueSum += queue; _processingSum += elapsed;
        _maxQueue = Math.Max(_maxQueue, queue); _maxProcessing = Math.Max(_maxProcessing, elapsed);
        if (age > _maxAge)
        {
            _maxAge = age; _worstEvent = item.EventTimeUtc; _worstReceived = item.ReceivedTimeUtc;
            _worstQueue = queue; _worstProcessing = elapsed;
        }
    }

    public TickLatencySummary Take()
    {
        var result = new TickLatencySummary(_count, _failed, _overTwoSeconds, _negativeQueue,
            _count == 0 ? null : _ageSum / _count, _count == 0 ? null : _maxAge,
            _count == 0 ? null : _queueSum / _count, _count == 0 ? null : _maxQueue,
            _count == 0 ? null : _processingSum / _count, _count == 0 ? null : _maxProcessing,
            _worstEvent, _worstReceived, _count == 0 ? null : _worstQueue, _count == 0 ? null : _worstProcessing);
        _count = _failed = _overTwoSeconds = _negativeQueue = 0;
        _ageSum = _queueSum = _processingSum = _maxProcessing = 0;
        _maxAge = _maxQueue = double.NegativeInfinity; _worstEvent = _worstReceived = null;
        _worstQueue = _worstProcessing = 0;
        return result;
    }
}

internal sealed record TickLatencySummary(long Events, long FailedProcessing, long ReceiveAgeOverTwoSeconds,
    long NegativeQueueClockDeltas, double? MeanReceiveMinusEventMs, double? MaxReceiveMinusEventMs,
    double? MeanDequeueMinusReceiptMs, double? MaxDequeueMinusReceiptMs,
    double? MeanPipelineMs, double? MaxPipelineMs, DateTime? OldestQuoteEventUtc, DateTime? OldestQuoteReceivedUtc,
    double? OldestQuoteDequeueMinusReceiptMs, double? OldestQuotePipelineMs);
