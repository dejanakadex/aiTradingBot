using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Tests.Fakes;
using Xunit;

namespace TradingBot.Tests
{
    public class IbkrConnectionServiceTests
    {
        [Fact]
        public async Task Connect_StartsAdapter_AndForwardsStatus()
        {
            var adapter = new FakeIbkrAdapter();
            var opts = new IbkrOptions { ReconnectDelayMs = 10, MaximumReconnectDelayMs = 100 };
            var svc = new IbkrConnectionService(adapter, opts, NullLogger<IbkrConnectionService>.Instance);

            ConnectionStatus? observed = null;
            svc.ConnectionStatusChanged += s => { observed = s; return Task.CompletedTask; };

            using var cts = new CancellationTokenSource(2000);
            await svc.ConnectAsync(cts.Token);

            Assert.Equal(ConnectionStatus.Connected, observed);

            // simulate disconnect -> service should attempt reconnect but adapter StartAsync is a no-op in fake.
            await adapter.SimulateDisconnectAsync();
            Assert.Equal(ConnectionStatus.Disconnected, observed);

            // simulate external reconnect
            await adapter.SimulateReconnectAsync();
            Assert.Equal(ConnectionStatus.Connected, observed);

            await svc.DisconnectAsync();
        }

        [Fact]
        public async Task Connect_WhenAlreadyConnected_IsIdempotent()
        {
            var adapter = new FakeIbkrAdapter();
            var opts = new IbkrOptions { ReconnectDelayMs = 10, MaximumReconnectDelayMs = 100 };
            var svc = new IbkrConnectionService(adapter, opts, NullLogger<IbkrConnectionService>.Instance);

            using var cts = new CancellationTokenSource(2000);
            await svc.ConnectAsync(cts.Token);
            await svc.ConnectAsync(cts.Token);

            Assert.Equal(ConnectionStatus.Connected, svc.Status);

            await svc.DisconnectAsync();
        }

        [Fact]
        public async Task DisconnectAfterSuccessfulConnect_StartsReconnectLoop()
        {
            var adapter = new FakeIbkrAdapter();
            var opts = new IbkrOptions { ReconnectDelayMs = 10, MaximumReconnectDelayMs = 100 };
            var svc = new IbkrConnectionService(adapter, opts, NullLogger<IbkrConnectionService>.Instance);

            using var cts = new CancellationTokenSource(2000);
            await svc.ConnectAsync(cts.Token);
            Assert.Equal(1, adapter.StartCalls);

            await adapter.SimulateDisconnectAsync();
            await WaitUntilAsync(() => adapter.StartCalls >= 2);

            Assert.Equal(ConnectionStatus.Connected, svc.Status);

            await svc.DisconnectAsync();
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(2000);
            while (!condition())
            {
                await Task.Delay(25, timeout.Token);
            }
        }
    }
}
