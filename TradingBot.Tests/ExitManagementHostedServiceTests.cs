using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class ExitManagementHostedServiceTests
    {
        [Fact]
        public async Task StartsDeadlineCheckWithoutWaitingForMarketData()
        {
            var exits = new FakeExitManagementService();
            using var hosted = new ExitManagementHostedService(exits,
                new TradingEngineStatusService(), NullLogger<ExitManagementHostedService>.Instance);

            await hosted.StartAsync(CancellationToken.None);
            var checkedAtUtc = await exits.Checked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await hosted.StopAsync(CancellationToken.None);

            Assert.True(exits.Restored);
            Assert.True(checkedAtUtc > DateTime.UtcNow.AddMinutes(-1));
        }

        private sealed class FakeExitManagementService : IExitManagementService
        {
            public bool Restored { get; private set; }
            public TaskCompletionSource<DateTime> Checked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task RestoreAsync(CancellationToken cancellationToken = default)
            {
                Restored = true;
                return Task.CompletedTask;
            }
            public Task ProcessExpiredPositionsAsync(DateTime asOfUtc, CancellationToken cancellationToken = default)
            {
                Checked.TrySetResult(asOfUtc);
                return Task.CompletedTask;
            }
            public Task RegisterApprovedEntryAsync(ApprovedTradePlan plan, ManagedOrderResult entryOrder,
                CancellationToken cancellationToken = default, int? approvedHoldingSeconds = null) => Task.CompletedTask;
            public Task ProcessMarketCandleAsync(Candle candle,
                CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task RequestCloseAllAsync(string reason,
                CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }
}
