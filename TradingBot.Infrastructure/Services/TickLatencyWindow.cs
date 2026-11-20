using TradingBot.Application.DTOs;

namespace TradingBot.Infrastructure.Services;

/// <summary>One collector's bounded minute accumulator. No quote prices, accounts or event buffering.</summary>
internal sealed class TickLatencyWindow
{
    private readonly LatencyHistogram _source = new(), _queue = new(), _pipeline = new(), _monotonicQueue = new();
    private readonly Dictionary<string, LatencyHistogram> _phases = Enum.GetValues<CollectionPhase>().ToDictionary(x => x.ToString(), _ => new LatencyHistogram());
    private readonly List<TickProcessingOutlier> _outliers = new();
    private TickProcessingOutlier? _worstPipelineEvent, _worstQueueEvent;
    private long _count, _failed, _overTwoSeconds, _negativeQueue;
    private double _ageSum, _queueSum, _processingSum, _maxAge = double.NegativeInfinity;
    private double _maxQueue = double.NegativeInfinity, _maxProcessing;
    private DateTime? _worstEvent, _worstReceived;
    private double _worstQueue, _worstProcessing;

    public void Record(CanonicalMarketDataEvent item, DateTime dequeuedUtc, TimeSpan processing, bool failed, Dictionary<string, double>? phases = null, double? monotonicQueueMs = null)
    {
        var age = (item.ReceivedTimeUtc - item.EventTimeUtc).TotalMilliseconds;
        var queue = (dequeuedUtc - item.ReceivedTimeUtc).TotalMilliseconds;
        var elapsed = processing.TotalMilliseconds;
        _source.Record(age); _queue.Record(queue); _pipeline.Record(elapsed);
        if (monotonicQueueMs.HasValue) _monotonicQueue.Record(monotonicQueueMs.Value);
        if (phases != null) foreach (var phase in phases) _phases[phase.Key].Record(phase.Value);
        var outlier = elapsed > 100 || queue > 100 || age > 2000;
        if (outlier || _worstPipelineEvent == null || elapsed > _worstPipelineEvent.PipelineMs
            || _worstQueueEvent == null || queue > _worstQueueEvent.QueueWallMs)
        {
            var sample = new TickProcessingOutlier(item.EventId, item.EventTimeUtc, item.ReceivedTimeUtc, dequeuedUtc, age, queue, monotonicQueueMs, elapsed, phases);
            if (_worstPipelineEvent == null || elapsed > _worstPipelineEvent.PipelineMs) _worstPipelineEvent = sample;
            if (_worstQueueEvent == null || queue > _worstQueueEvent.QueueWallMs) _worstQueueEvent = sample;
            if (outlier)
            {
                _outliers.Add(sample);
                _outliers.Sort((a, b) => Math.Max(b.PipelineMs, b.QueueWallMs).CompareTo(Math.Max(a.PipelineMs, a.QueueWallMs)));
                if (_outliers.Count > 3) _outliers.RemoveAt(3);
            }
        }
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
            _worstEvent, _worstReceived, _count == 0 ? null : _worstQueue, _count == 0 ? null : _worstProcessing)
        { SourceAge = _source.Take(), QueueWallClock = _queue.Take(), QueueMonotonic = _monotonicQueue.Take(), Pipeline = _pipeline.Take(),
            Phases = _phases.ToDictionary(x => x.Key, x => x.Value.Take()), SlowEvents = _outliers.ToArray(),
            WorstPipelineEvent = _worstPipelineEvent, WorstQueueEvent = _worstQueueEvent };
        _worstPipelineEvent = _worstQueueEvent = null;
        _outliers.Clear();
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
    double? OldestQuoteDequeueMinusReceiptMs, double? OldestQuotePipelineMs)
{
    public string Version => "collection-timing-v2";
    public int ProcessId => Environment.ProcessId;
    public LatencyPercentiles? SourceAge { get; init; }
    public LatencyPercentiles? QueueWallClock { get; init; }
    public LatencyPercentiles? QueueMonotonic { get; init; }
    public LatencyPercentiles? Pipeline { get; init; }
    public Dictionary<string, LatencyPercentiles>? Phases { get; init; }
    public TickProcessingOutlier? WorstPipelineEvent { get; init; }
    public TickProcessingOutlier? WorstQueueEvent { get; init; }
    public TickProcessingOutlier[] SlowEvents { get; init; } = [];
}
internal sealed record TickProcessingOutlier(string EventId, DateTime EventUtc, DateTime ReceivedUtc, DateTime DequeuedUtc,
    double SourceAgeMs, double QueueWallMs, double? QueueMonotonicMs, double PipelineMs, Dictionary<string, double>? Phases);
