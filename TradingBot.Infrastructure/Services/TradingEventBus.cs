using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public class TradingEventBus : ITradingEventBus, IDisposable
    {
        private readonly Channel<Candle> _candles;
        private readonly Channel<PatternCandidate> _patterns;
        private readonly Channel<AiMarketAnalysis> _analyses;
        private readonly Channel<TradeSignal> _signals;
        private readonly Channel<OrderRequest> _orders;

        private readonly TradingEventBusOptions _options;
        private readonly ILogger<TradingEventBus> _logger;

        private readonly object _candleLock = new();
        private readonly object _patternLock = new();
        private readonly object _analysisLock = new();
        private readonly object _signalLock = new();
        private readonly object _orderLock = new();

        public TradingEventBus(TradingEventBusOptions? options, ILogger<TradingEventBus> logger)
        {
            _options = options ?? new TradingEventBusOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _candles = Channel.CreateBounded<Candle>(new BoundedChannelOptions(_options.CandleCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _patterns = Channel.CreateBounded<PatternCandidate>(new BoundedChannelOptions(_options.PatternCandidateCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _analyses = Channel.CreateBounded<AiMarketAnalysis>(new BoundedChannelOptions(_options.AiMarketAnalysisCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _signals = Channel.CreateBounded<TradeSignal>(new BoundedChannelOptions(_options.TradeSignalCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

            _orders = Channel.CreateBounded<OrderRequest>(new BoundedChannelOptions(_options.OrderRequestCapacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        public ChannelReader<Candle> CandleReader => _candles.Reader;
        public ChannelReader<PatternCandidate> PatternCandidateReader => _patterns.Reader;
        public ChannelReader<AiMarketAnalysis> AiMarketAnalysisReader => _analyses.Reader;
        public ChannelReader<TradeSignal> TradeSignalReader => _signals.Reader;
        public ChannelReader<OrderRequest> OrderRequestReader => _orders.Reader;

        public bool TryPublish(Candle candle)
        {
            if (_candles.Writer.TryWrite(candle)) return true;
            lock (_candleLock)
            {
                if (_candles.Writer.TryWrite(candle)) return true;
                return HandleFull<Candle>(_candles, candle, _options.FullMode, _candleLock, nameof(Candle));
            }
        }

        public async ValueTask<bool> PublishAsync(Candle candle, CancellationToken cancellationToken = default)
        {
            return await PublishAsyncInternal(_candles, candle, _options.FullMode, _candleLock, cancellationToken, nameof(Candle));
        }

        public bool TryPublish(PatternCandidate pattern)
        {
            if (_patterns.Writer.TryWrite(pattern)) return true;
            lock (_patternLock)
            {
                if (_patterns.Writer.TryWrite(pattern)) return true;
                return HandleFull<PatternCandidate>(_patterns, pattern, _options.FullMode, _patternLock, nameof(PatternCandidate));
            }
        }

        public async ValueTask<bool> PublishAsync(PatternCandidate pattern, CancellationToken cancellationToken = default)
        {
            return await PublishAsyncInternal(_patterns, pattern, _options.FullMode, _patternLock, cancellationToken, nameof(PatternCandidate));
        }

        public bool TryPublish(AiMarketAnalysis analysis)
        {
            if (_analyses.Writer.TryWrite(analysis)) return true;
            lock (_analysisLock)
            {
                if (_analyses.Writer.TryWrite(analysis)) return true;
                return HandleFull<AiMarketAnalysis>(_analyses, analysis, _options.FullMode, _analysisLock, nameof(AiMarketAnalysis));
            }
        }

        public async ValueTask<bool> PublishAsync(AiMarketAnalysis analysis, CancellationToken cancellationToken = default)
        {
            return await PublishAsyncInternal(_analyses, analysis, _options.FullMode, _analysisLock, cancellationToken, nameof(AiMarketAnalysis));
        }

        public bool TryPublish(TradeSignal signal)
        {
            if (_signals.Writer.TryWrite(signal)) return true;
            lock (_signalLock)
            {
                if (_signals.Writer.TryWrite(signal)) return true;
                return HandleFull<TradeSignal>(_signals, signal, _options.FullMode, _signalLock, nameof(TradeSignal));
            }
        }

        public async ValueTask<bool> PublishAsync(TradeSignal signal, CancellationToken cancellationToken = default)
        {
            return await PublishAsyncInternal(_signals, signal, _options.FullMode, _signalLock, cancellationToken, nameof(TradeSignal));
        }

        public bool TryPublish(OrderRequest order)
        {
            if (_orders.Writer.TryWrite(order)) return true;
            lock (_orderLock)
            {
                if (_orders.Writer.TryWrite(order)) return true;
                return HandleFull<OrderRequest>(_orders, order, _options.FullMode, _orderLock, nameof(OrderRequest));
            }
        }

        public async ValueTask<bool> PublishAsync(OrderRequest order, CancellationToken cancellationToken = default)
        {
            return await PublishAsyncInternal(_orders, order, _options.FullMode, _orderLock, cancellationToken, nameof(OrderRequest));
        }

        private bool HandleFull<T>(Channel<T> channel, T item, ChannelFullMode mode, object syncLock, string name)
        {
            switch (mode)
            {
                case ChannelFullMode.Reject:
                    _logger.LogWarning("{Name} dropped: channel full, rejecting new item", name);
                    return false;
                case ChannelFullMode.DropNewest:
                    _logger.LogWarning("{Name} dropped: channel full, dropping newest item", name);
                    return false;
                case ChannelFullMode.DropOldest:
                    if (channel.Reader.TryRead(out var dropped))
                    {
                        _logger.LogInformation("{Name} dropped oldest item to make room", name);
                        var wrote = channel.Writer.TryWrite(item);
                        if (!wrote)
                        {
                            _logger.LogWarning("{Name} write failed after dropping oldest", name);
                        }
                        return wrote;
                    }
                    _logger.LogWarning("{Name} channel full but nothing to drop", name);
                    return false;
                case ChannelFullMode.Wait:
                default:
                    _logger.LogWarning("{Name} channel is full and configured for Wait; TryPublish will reject to avoid blocking", name);
                    return false;
            }
        }

        private async ValueTask<bool> PublishAsyncInternal<T>(Channel<T> channel, T item, ChannelFullMode mode, object syncLock, CancellationToken cancellationToken, string name)
        {
            switch (mode)
            {
                case ChannelFullMode.Wait:
                    try
                    {
                        await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
                        return true;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogWarning("{Name} publish canceled while waiting", name);
                        return false;
                    }
                case ChannelFullMode.Reject:
                    if (channel.Writer.TryWrite(item)) return true;
                    _logger.LogWarning("{Name} publish rejected: channel full", name);
                    return false;
                case ChannelFullMode.DropNewest:
                    if (channel.Writer.TryWrite(item)) return true;
                    _logger.LogWarning("{Name} publish dropped newest: channel full", name);
                    return false;
                case ChannelFullMode.DropOldest:
                    if (channel.Writer.TryWrite(item)) return true;
                    lock (syncLock)
                    {
                        if (channel.Writer.TryWrite(item)) return true;
                        if (channel.Reader.TryRead(out var dropped))
                        {
                            _logger.LogInformation("{Name} dropped oldest item to make room (async)", name);
                            // attempt to write now
                            if (channel.Writer.TryWrite(item)) return true;
                        }
                    }
                    _logger.LogWarning("{Name} publish failed after attempting drop-oldest", name);
                    return false;
                default:
                    _logger.LogWarning("{Name} unknown full-mode, rejecting", name);
                    return false;
            }
        }

        public void Dispose()
        {
            try
            {
                _candles.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to complete candle channel during disposal");
            }

            TryComplete(_patterns, nameof(PatternCandidate));
            TryComplete(_analyses, nameof(AiMarketAnalysis));
            TryComplete(_signals, nameof(TradeSignal));
            TryComplete(_orders, nameof(OrderRequest));
        }

        private void TryComplete<T>(Channel<T> channel, string name)
        {
            try
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to complete {Name} channel during disposal", name);
            }
        }
    }
}
