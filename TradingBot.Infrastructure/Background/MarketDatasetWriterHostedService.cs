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
        private readonly Channel<MarketDatasetRecord> _channel;

        public MarketDatasetWriterHostedService(
            IMarketDatasetStore store,
            IOptions<DatasetStorageSettings> settings,
            ILogger<MarketDatasetWriterHostedService> logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _channel = Channel.CreateBounded<MarketDatasetRecord>(new BoundedChannelOptions(_settings.QueueCapacity)
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
                if (_channel.Writer.TryWrite(record)) return true;
            }
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled) return;
            var batch = new List<MarketDatasetRecord>(_settings.BatchSize);
            var flushInterval = TimeSpan.FromSeconds(_settings.FlushIntervalSeconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    while (batch.Count < _settings.BatchSize && _channel.Reader.TryRead(out var record))
                    {
                        batch.Add(record);
                    }

                    if (batch.Count >= _settings.BatchSize)
                    {
                        await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var canRead = _channel.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    var delay = Task.Delay(flushInterval, stoppingToken);
                    var completed = await Task.WhenAny(canRead, delay).ConfigureAwait(false);
                    if (completed == delay && batch.Count > 0)
                    {
                        await FlushAsync(batch, stoppingToken).ConfigureAwait(false);
                    }
                    else if (completed == canRead && !await canRead.ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown continues below and drains the bounded queue.
            }
            finally
            {
                while (_channel.Reader.TryRead(out var record)) batch.Add(record);
                if (batch.Count > 0)
                {
                    try
                    {
                        await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical(ex, "Failed to flush {RecordCount} market dataset records during shutdown.", batch.Count);
                    }
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.TryComplete();
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FlushAsync(List<MarketDatasetRecord> batch, CancellationToken cancellationToken)
        {
            if (batch.Count == 0) return;
            var write = batch.ToArray();
            await _store.WriteBatchAsync(write, cancellationToken).ConfigureAwait(false);
            batch.Clear();
            _logger.LogDebug("Flushed {RecordCount} canonical market-data records to the Parquet dataset.", write.Length);
        }
    }
}
