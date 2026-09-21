using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Tests.Fakes
{
    public class FakeIbkrConnectionService : IIbkrConnectionService
    {
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

        public event Func<TradingBot.Application.DTOs.ConnectionStatus, Task>? ConnectionStatusChanged;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Connected;
            return ConnectionStatusChanged?.Invoke(Status) ?? Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Disconnected;
            return ConnectionStatusChanged?.Invoke(Status) ?? Task.CompletedTask;
        }
    }

    public class FakeMarketDataService : IMarketDataService
    {
        public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            var channel = Channel.CreateUnbounded<MarketBar>();
            var sub = new SimpleSubscription(channel.Reader, channel);
            return Task.FromResult<IMarketDataSubscription>(sub);
        }

        public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(string symbol, string timeframe, int count, CancellationToken cancellationToken = default)
        {
            var list = new List<MarketBar>();
            for (int i = 0; i < count; i++) list.Add(new MarketBar { Symbol = symbol, Timeframe = timeframe, TimestampUtc = DateTime.UtcNow, Open = 1, High = 2, Low = 0.5m, Close = 1.5m, Volume = 10 });
            return Task.FromResult<IEnumerable<MarketBar>>(list);
        }

        private class SimpleSubscription : IMarketDataSubscription
        {
            public SimpleSubscription(ChannelReader<MarketBar> reader, Channel<MarketBar> channel)
            {
                Reader = reader;
                _channel = channel;
            }

            private readonly Channel<MarketBar> _channel;

            public ChannelReader<MarketBar> Reader { get; }

            public ValueTask DisposeAsync()
            {
                _channel.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }
}
