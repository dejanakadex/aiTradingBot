using System;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class TradingPipelineStatusServiceTests
    {
        [Fact]
        public void Current_StartsWaitingForCandle()
        {
            var clock = new MutableClock(new DateTime(2026, 8, 27, 14, 30, 0, DateTimeKind.Utc));
            var service = new TradingPipelineStatusService(clock);

            var snapshot = service.Current;

            Assert.Equal(TradingPipelineStage.WaitingForCandle, snapshot.Stage);
            Assert.Equal(TradingPipelineActivityState.Waiting, snapshot.State);
            Assert.Contains("Waiting", snapshot.Status);
        }

        [Fact]
        public void Current_ReturnsTerminalStateBrieflyThenResetsToWaiting()
        {
            var clock = new MutableClock(new DateTime(2026, 8, 27, 14, 30, 0, DateTimeKind.Utc));
            var service = new TradingPipelineStatusService(clock);

            service.Mark(
                TradingPipelineStage.PatternAnalysis,
                TradingPipelineActivityState.Rejected,
                "Rejected before AI.",
                "SPY",
                "Hammer");

            Assert.Equal(TradingPipelineActivityState.Rejected, service.Current.State);
            Assert.Equal(TradingPipelineStage.PatternAnalysis, service.Current.Stage);

            clock.UtcNow = clock.UtcNow.AddSeconds(9);

            var snapshot = service.Current;
            Assert.Equal(TradingPipelineActivityState.Waiting, snapshot.State);
            Assert.Equal(TradingPipelineStage.WaitingForCandle, snapshot.Stage);
        }

        private sealed class MutableClock : IClock
        {
            public MutableClock(DateTime utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTime UtcNow { get; set; }
        }
    }
}
