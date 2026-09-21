using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class TradingEventBusTests
    {
        [Fact]
        public async Task PublishAndConsume_Candle_Succeeds()
        {
            var opts = new TradingEventBusOptions { CandleCapacity = 10 };
            var bus = new TradingEventBus(opts, NullLogger<TradingEventBus>.Instance);

            var candle = new Candle("X", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m, 2m, 0.5m, 1.5m, 10m);

            var written = bus.TryPublish(candle);
            Assert.True(written);

            var c = await bus.CandleReader.ReadAsync(new CancellationTokenSource(5000).Token);
            Assert.Equal("X", c.Symbol);
        }

        [Fact]
        public async Task DropOldestBehavior_RemovesOldestWhenFull()
        {
            var opts = new TradingEventBusOptions { CandleCapacity = 2, FullMode = ChannelFullMode.DropOldest };
            var bus = new TradingEventBus(opts, NullLogger<TradingEventBus>.Instance);

            var c1 = new Candle("1", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m,2m,0.5m,1.5m,1m);
            var c2 = new Candle("2", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m,2m,0.5m,1.5m,1m);
            var c3 = new Candle("3", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m,2m,0.5m,1.5m,1m);

            Assert.True(bus.TryPublish(c1));
            Assert.True(bus.TryPublish(c2));
            // now full; next publish should drop oldest (c1)
            Assert.True(bus.TryPublish(c3));

            var list = new List<Candle>();
            var cA = await bus.CandleReader.ReadAsync(new CancellationTokenSource(5000).Token);
            var cB = await bus.CandleReader.ReadAsync(new CancellationTokenSource(5000).Token);
            list.Add(cA); list.Add(cB);

            var symbols = list.Select(x => x.Symbol).ToList();
            Assert.DoesNotContain("1", symbols);
            Assert.Contains("2", symbols);
            Assert.Contains("3", symbols);
        }

        [Fact]
        public void RejectMode_PreventsNewWritesWhenFull()
        {
            var opts = new TradingEventBusOptions { CandleCapacity = 1, FullMode = ChannelFullMode.Reject };
            var bus = new TradingEventBus(opts, NullLogger<TradingEventBus>.Instance);

            var c1 = new Candle("A", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m,2m,0.5m,1.5m,1m);
            var c2 = new Candle("B", TradingBot.Domain.Enums.Timeframe.OneMinute, DateTime.UtcNow, 1m,2m,0.5m,1.5m,1m);

            Assert.True(bus.TryPublish(c1));
            Assert.False(bus.TryPublish(c2));
        }
    }
}
