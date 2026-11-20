using System.Diagnostics;

namespace TradingBot.Infrastructure.Services;

// Fixed-memory histogram of all observations, not a last-N sample. Quantiles are bucket upper bounds.
internal sealed class LatencyHistogram
{
    private static readonly double[] Bounds = Enumerable.Range(0, 190).Select(i => .01 * Math.Pow(1.1, i)).ToArray();
    private readonly long[] _buckets = new long[Bounds.Length + 1];
    private long _count;
    private double _max;
    public void Record(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0) return;
        var index = Array.BinarySearch(Bounds, ms);
        if (index < 0) index = ~index;
        _buckets[index]++; _count++; _max = Math.Max(_max, ms);
    }
    public LatencyPercentiles Take()
    {
        double? Percentile(double fraction)
        {
            if (_count == 0) return null;
            var rank = (long)Math.Ceiling(_count * fraction); long seen = 0;
            for (var i = 0; i < _buckets.Length; i++)
            {
                seen += _buckets[i];
                if (seen >= rank) return Math.Min(_max, i < Bounds.Length ? Bounds[i] : _max);
            }
            return _max;
        }
        var result = new LatencyPercentiles(_count, Percentile(.5), Percentile(.95), Percentile(.99), _count == 0 ? null : _max);
        Array.Clear(_buckets); _count = 0; _max = 0;
        return result;
    }
}

internal sealed record LatencyPercentiles(long Count, double? P50UpperMs, double? P95UpperMs, double? P99UpperMs, double? MaxMs);

internal enum CollectionPhase { Quality, QualityGate, QualityRead, QualityAssess, QualitySave, DatasetEnqueue, Aggregates, DatasetWriteGate, ManifestRead, ParquetSerialize, ManifestWrite }

// A collector-local scope flows across awaits; no timing data is persisted as market data or used in decisions.
internal sealed class CollectionTiming : IDisposable
{
    private static readonly AsyncLocal<CollectionTiming?> Current = new();
    private readonly CollectionTiming? _previous;
    private readonly double?[] _milliseconds = new double?[Enum.GetValues<CollectionPhase>().Length];
    public CollectionTiming() { _previous = Current.Value; Current.Value = this; }
    public Dictionary<string, double> Snapshot() => Enum.GetValues<CollectionPhase>()
        .Where(x => _milliseconds[(int)x].HasValue).ToDictionary(x => x.ToString(), x => _milliseconds[(int)x]!.Value);
    public static Measurement Measure(CollectionPhase phase) => new(Current.Value, phase);
    public void Dispose() => Current.Value = _previous;
    internal readonly struct Measurement(CollectionTiming? owner, CollectionPhase phase) : IDisposable
    {
        private readonly long _start = owner == null ? 0 : Stopwatch.GetTimestamp();
        public void Dispose()
        {
            if (owner != null) owner._milliseconds[(int)phase] = (owner._milliseconds[(int)phase] ?? 0)
                + Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
        }
    }
}

internal sealed class CollectionProcessSampler
{
    private TimeSpan _cpu;
    private long _last = Stopwatch.GetTimestamp(), _allocated = GC.GetTotalAllocatedBytes();
    private readonly int[] _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    public CollectionProcessSampler() { using var p = Process.GetCurrentProcess(); _cpu = p.TotalProcessorTime; }
    public CollectionProcessSnapshot Take()
    {
        using var p = Process.GetCurrentProcess();
        var elapsed = Stopwatch.GetElapsedTime(_last).TotalSeconds; _last = Stopwatch.GetTimestamp();
        var cpu = p.TotalProcessorTime; var allocated = GC.GetTotalAllocatedBytes();
        var gc = Enumerable.Range(0, 3).Select(i => { var count = GC.CollectionCount(i); var delta = count - _collections[i]; _collections[i] = count; return delta; }).ToArray();
        long? diskFree = null, logBytes = null;
        try
        {
            diskFree = new DriveInfo(Path.GetPathRoot(Path.GetFullPath("."))!).AvailableFreeSpace;
            var logs = Environment.GetEnvironmentVariable("TRADINGBOT_LOG_DIR") ?? "logs";
            if (Directory.Exists(logs)) logBytes = new DirectoryInfo(logs).EnumerateFiles("*.log").Sum(x => x.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Missing telemetry is null, never a collection failure. */ }
        var result = new CollectionProcessSnapshot(Environment.ProcessId,
            elapsed > 0 ? (cpu - _cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100 : 0,
            p.WorkingSet64, allocated - _allocated, gc, ThreadPool.PendingWorkItemCount, diskFree, logBytes,
            diskFree.HasValue && diskFree < 5L * 1024 * 1024 * 1024, logBytes.HasValue && logBytes > 2L * 1024 * 1024 * 1024);
        _cpu = cpu; _allocated = allocated; return result;
    }
}

internal sealed record CollectionProcessSnapshot(int ProcessId, double CpuPercentOfMachine, long WorkingSetBytes,
    long AllocatedBytesDelta, int[] GcCollectionsDelta, long ThreadPoolPending, long? DiskFreeBytes, long? LogBytes,
    bool DiskLow, bool LogsLarge);
