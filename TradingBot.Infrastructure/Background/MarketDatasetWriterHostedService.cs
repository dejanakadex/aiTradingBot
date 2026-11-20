using System.Diagnostics;
using System.Text.Json;
using TradingBot.Infrastructure.Services;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class MarketDatasetWriterHostedService : BackgroundService, IMarketDatasetSink
    {
        private readonly IMarketDatasetStore _store;
        private readonly DatasetStorageSettings _settings;
        private readonly ILogger<MarketDatasetWriterHostedService> _logger;
        private readonly Channel<QueuedRecord> _channel;

        private sealed record QueuedRecord(MarketDatasetRecord Record, long Enqueued);
        private readonly LatencyHistogram _writes = new(), _durableAge = new();
        private readonly Dictionary<string, LatencyHistogram> _phases = new();
        private readonly Stopwatch _report = Stopwatch.StartNew();
        private long _rows, _failures;
        private object? _worst;
        private double _maxWrite;

        public MarketDatasetWriterHostedService(
            IMarketDatasetStore store,
            IOptions<DatasetStorageSettings> settings,
            ILogger<MarketDatasetWriterHostedService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _channel = Channel.CreateBounded<QueuedRecord>(new BoundedChannelOptions(_settings.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public async ValueTask<bool> EnqueueAsync(MarketDatasetRecord record, CancellationToken cancellationToken = default)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!_settings.Enabled) return false;
            while (await _channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_channel.Writer.TryWrite(new(record, Stopwatch.GetTimestamp()))) return true;
            }
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled) return;
            var batch = new List<QueuedRecord>(_settings.BatchSize);
            long firstBuffered = 0;
            var flushInterval = TimeSpan.FromSeconds(_settings.FlushIntervalSeconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (batch.Count == 0 && !await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false)) break;
                    while (batch.Count < _settings.BatchSize && _channel.Reader.TryRead(out var record))
                    {
                        if (batch.Count == 0) firstBuffered = Stopwatch.GetTimestamp();
                        batch.Add(record);
                        if (Stopwatch.GetElapsedTime(firstBuffered) >= flushInterval) break;
                    }
                    if (batch.Count == 0) continue;
                    var remaining = flushInterval - Stopwatch.GetElapsedTime(firstBuffered);
                    if (batch.Count >= _settings.BatchSize || remaining <= TimeSpan.Zero)
                    {
                        // Finish an in-flight file/manifest commit before draining on graceful shutdown.
                        await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                        continue;
                    }
                    // Deadline belongs to the oldest buffered row, not the latest arrival. Dispose the timer on every wake.
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    wait.CancelAfter(remaining);
                    try
                    {
                        if (!await _channel.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break;
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Finish an in-flight file/manifest commit before draining on graceful shutdown.
                        await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown continues below and drains the bounded queue.
            }
            finally
            {
                try
                {
                    while (_channel.Reader.TryRead(out var record))
                    {
                        batch.Add(record);
                        if (batch.Count >= _settings.BatchSize) await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    if (batch.Count > 0) await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "Failed to flush {RecordCount} buffered market dataset records during shutdown; queued records may also remain.", batch.Count);
                }
                Report(force: true);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.TryComplete();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        private void Report(bool force = false)
        {
            if (!force && _report.Elapsed < TimeSpan.FromMinutes(1)) return;
            _logger.Log(_failures > 0 || _maxWrite > 1000 ? LogLevel.Warning : LogLevel.Information,
                "Dataset storage latency AsOfUtc={AsOfUtc:O} Metrics={Metrics}", DateTime.UtcNow,
                JsonSerializer.Serialize(new { Version = "collection-timing-v2", ProcessId = Environment.ProcessId, ElapsedSeconds = _report.Elapsed.TotalSeconds,
                    WrittenRows = _rows, Failures = _failures, QueueDepth = _channel.Reader.Count,
                    Flush = _writes.Take(), EnqueueToDurable = _durableAge.Take(),
                    Phases = _phases.ToDictionary(x => x.Key, x => x.Value.Take()), SlowestFlush = _worst }));
            _report.Restart(); _rows = _failures = 0; _worst = null; _maxWrite = 0;
        }

        private async Task FlushAsync(List<QueuedRecord> batch, CancellationToken cancellationToken)
        {
            if (batch.Count == 0) return;
            var started = Stopwatch.GetTimestamp(); var failed = true;
            using var timing = new CollectionTiming();
            try
            {
                await _store.WriteBatchAsync(batch.Select(x => x.Record).ToArray(), cancellationToken).ConfigureAwait(false);
                var completed = Stopwatch.GetTimestamp();
                foreach (var row in batch) _durableAge.Record(Stopwatch.GetElapsedTime(row.Enqueued, completed).TotalMilliseconds);
                _rows += batch.Count; failed = false;
            }
            finally
            {
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var phases = timing.Snapshot();
                foreach (var phase in phases)
                {
                    if (!_phases.TryGetValue(phase.Key, out var histogram)) _phases[phase.Key] = histogram = new();
                    histogram.Record(phase.Value);
                }
                _writes.Record(ms); if (failed) _failures++;
                if (ms >= _maxWrite) { _maxWrite = ms; _worst = new { AtUtc = DateTime.UtcNow, Milliseconds = ms, Rows = batch.Count, Failed = failed, Phases = phases }; }
                if (!failed) batch.Clear();
                Report();
            }
        }
    }
}
