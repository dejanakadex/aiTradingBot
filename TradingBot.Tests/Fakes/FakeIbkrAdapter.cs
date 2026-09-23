using System;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Tests.Fakes
{
    public class FakeIbkrAdapter : IIbkrAdapter
    {
        public bool IsConnected { get; private set; }
        public int StartCalls { get; private set; }

        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
        public event Func<bool, Task>? ReadinessChanged;
        public event Func<TradingBot.Application.DTOs.MarketBar, Task>? MarketBarReceived;
        public event Func<CanonicalMarketDataEvent, Task>? MarketDataEventReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            IsConnected = true;
            _ = ConnectionStatusChanged?.Invoke(ConnectionStatus.Connected);
            _ = ReadinessChanged?.Invoke(true);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            _ = ConnectionStatusChanged?.Invoke(ConnectionStatus.Disconnected);
            _ = ReadinessChanged?.Invoke(false);
            return Task.CompletedTask;
        }

        // Helper to simulate connection loss
        public Task SimulateDisconnectAsync()
        {
            IsConnected = false;
            return ConnectionStatusChanged?.Invoke(ConnectionStatus.Disconnected) ?? Task.CompletedTask;
        }

        // Helper to simulate external reconnect
        public Task SimulateReconnectAsync()
        {
            IsConnected = true;
            return ConnectionStatusChanged?.Invoke(ConnectionStatus.Connected) ?? Task.CompletedTask;
        }

        public Task SimulateMarketBarAsync(TradingBot.Application.DTOs.MarketBar bar)
        {
            return MarketBarReceived?.Invoke(bar) ?? Task.CompletedTask;
        }

        public Task SimulateMarketDataEventAsync(CanonicalMarketDataEvent marketEvent)
        {
            return MarketDataEventReceived?.Invoke(marketEvent) ?? Task.CompletedTask;
        }
    }
}
