using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public class MarketDataService : IMarketDataService
    {
        private readonly ILogger<MarketDataService> _logger;

        public MarketDataService(ILogger<MarketDataService> logger)
        {
            _logger = logger;
        }

        public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("GetHistoricalBarsAsync placeholder called for {Symbol} {Timeframe} [{StartUtc}, {EndUtc})", request.Symbol, request.Timeframe, request.StartUtc, request.EndUtc);
            return Task.FromResult<IEnumerable<MarketBar>>(Array.Empty<MarketBar>());
        }

        public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("SubscribeAsync placeholder called for {symbol} {timeframe}", symbol, timeframe);
            // return an empty subscription that completes immediately
            var channel = Channel.CreateUnbounded<MarketBar>();
            channel.Writer.TryComplete();
            IMarketDataSubscription sub = new SimpleSubscription(channel.Reader, channel);
            return Task.FromResult(sub);
        }

        private sealed class SimpleSubscription : IMarketDataSubscription
        {
            private readonly Channel<MarketBar> _channel;

            public SimpleSubscription(ChannelReader<MarketBar> reader, Channel<MarketBar> channel)
            {
                Reader = reader;
                _channel = channel;
            }

            public ChannelReader<MarketBar> Reader { get; }

            public ValueTask DisposeAsync()
            {
                _channel.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }
}
