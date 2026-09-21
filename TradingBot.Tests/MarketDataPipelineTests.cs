using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Services;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Models;
using TradingBot.Tests.Fakes;
using Xunit;

namespace TradingBot.Tests
{
    public class MarketDataPipelineTests
    {
        private IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection conn)
        {
            conn = new SqliteConnection("Data Source=:memory:");
            conn.Open();
            var builder = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>();
            builder.UseSqlite(conn);
            var options = builder.Options;

            var factory = new SimpleDbContextFactory(options);

            using var ctx = factory.CreateDbContext();
            ctx.Database.EnsureCreated();
            return factory;
        }

        private class SimpleDbContextFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;
            public SimpleDbContextFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options) => _options = options;
            public TradingBot.Persistence.TradingBotDbContext CreateDbContext() => new TradingBot.Persistence.TradingBotDbContext(_options);
            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => new ValueTask<TradingBot.Persistence.TradingBotDbContext>(Task.FromResult(CreateDbContext()));
        }

        [Fact]
        public async Task CandleNormalizationAndPersistence_WorksAndPreventsDuplicates()
        {
            var adapter = new FakeIbkrAdapter();
            var eventBusOpts = new TradingBot.Infrastructure.Options.TradingEventBusOptions();
            var eventBus = new TradingBot.Infrastructure.Services.TradingEventBus(eventBusOpts, NullLogger<TradingBot.Infrastructure.Services.TradingEventBus>.Instance);

            var factory = CreateInMemoryFactory(out var conn);
            var pipeline = new MarketDataPipeline(adapter, eventBus, factory, NullLogger<MarketDataPipeline>.Instance);

            // subscribe to event bus to capture published candles
            var reader = eventBus.CandleReader;

            var bar = new MarketBar { Symbol = "SPY", Timeframe = "1m", TimestampUtc = DateTime.UtcNow, Open = 1, High = 2, Low = 0.5m, Close = 1.5m, Volume = 100 };

            // simulate adapter emitting a market bar
            await adapter.SimulateMarketBarAsync(bar);

            // read published candle
            var published = await reader.ReadAsync(new System.Threading.CancellationTokenSource(2000).Token);
            Assert.Equal("SPY", published.Symbol);

            // Verify persisted
            await using (var ctx = factory.CreateDbContext())
            {
                var count = await ctx.Candles.CountAsync(c => c.Symbol == "SPY");
                Assert.Equal(1, count);
            }

            // Emit duplicate
            await adapter.SimulateMarketBarAsync(bar);
            await using (var ctx = factory.CreateDbContext())
            {
                var count = await ctx.Candles.CountAsync(c => c.Symbol == "SPY");
                Assert.Equal(1, count);
            }

            conn.Close();
        }

        [Fact]
        public async Task ProcessMarketBarAsync_PersistsCandleBeforePublishingToAnalysisPipeline()
        {
            var adapter = new FakeIbkrAdapter();
            var factory = CreateInMemoryFactory(out var conn);
            var eventBus = new PersistCheckingEventBus(factory);
            var pipeline = new MarketDataPipeline(adapter, eventBus, factory, NullLogger<MarketDataPipeline>.Instance);
            var timestamp = DateTime.UtcNow;

            await pipeline.ProcessMarketBarAsync(new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = timestamp,
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100.5m,
                Volume = 1000m
            });

            Assert.True(eventBus.SawPersistedCandleBeforePublish);
            Assert.True(eventBus.CandleReader.TryRead(out var published));
            Assert.Equal(timestamp, published.TimestampUtc);

            conn.Close();
        }

        [Fact]
        public async Task HistoricalSeed_PersistsCandleWithoutPublishingTradingEvent()
        {
            var adapter = new FakeIbkrAdapter();
            var eventBusOpts = new TradingBot.Infrastructure.Options.TradingEventBusOptions();
            var eventBus = new TradingBot.Infrastructure.Services.TradingEventBus(eventBusOpts, NullLogger<TradingBot.Infrastructure.Services.TradingEventBus>.Instance);

            var factory = CreateInMemoryFactory(out var conn);
            var pipeline = new MarketDataPipeline(adapter, eventBus, factory, NullLogger<MarketDataPipeline>.Instance);
            var bar = new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = DateTime.UtcNow.AddMinutes(-30),
                Open = 1,
                High = 2,
                Low = 0.5m,
                Close = 1.5m,
                Volume = 100
            };

            await pipeline.ProcessMarketBarAsync(
                bar,
                allowStaleSeedCandle: true,
                publishToEventBus: false);

            Assert.False(eventBus.CandleReader.TryRead(out _));
            await using (var ctx = factory.CreateDbContext())
            {
                var count = await ctx.Candles.CountAsync(c => c.Symbol == "SPY");
                Assert.Equal(1, count);
            }

            eventBus.Dispose();
            conn.Close();
        }

        [Fact]
        public async Task CandleHistoryService_ReturnsLastN()
        {
            var factory = CreateInMemoryFactory(out var conn);
            // seed data
            await using (var ctx = factory.CreateDbContext())
            {
                for (int i = 0; i < 5; i++)
                {
                    ctx.Candles.Add(new TradingBot.Persistence.Candle { Symbol = "SPY", Timeframe = TradingBot.Domain.Enums.Timeframe.OneMinute, TimestampUtc = DateTime.UtcNow.AddMinutes(-i), Open = 1, High = 2, Low = 0.5m, Close = 1.5m, Volume = 10 });
                }
                await ctx.SaveChangesAsync();
            }

            var history = new CandleHistoryService(factory);
            var last3 = await history.GetLastNCandlesAsync("SPY", TradingBot.Domain.Enums.Timeframe.OneMinute, 3);
            Assert.Equal(3, last3.Count);

            conn.Close();
        }

        [Fact]
        public async Task CandleHistoryService_TreatsSqliteUnspecifiedTimestampAsUtc()
        {
            var factory = CreateInMemoryFactory(out var conn);
            var timestamp = new DateTime(2026, 8, 27, 14, 42, 0, DateTimeKind.Unspecified);

            await using (var ctx = factory.CreateDbContext())
            {
                ctx.Candles.Add(new TradingBot.Persistence.Candle
                {
                    Symbol = "SPY",
                    Timeframe = TradingBot.Domain.Enums.Timeframe.OneMinute,
                    TimestampUtc = timestamp,
                    Open = 100m,
                    High = 101m,
                    Low = 99m,
                    Close = 100.5m,
                    Volume = 1000m
                });
                await ctx.SaveChangesAsync();
            }

            var history = new CandleHistoryService(factory);
            var candles = await history.GetLastNCandlesAsync("SPY", TradingBot.Domain.Enums.Timeframe.OneMinute, 1);

            Assert.Single(candles);
            Assert.Equal(DateTimeKind.Utc, candles[0].TimestampUtc.Kind);
            Assert.Equal(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc), candles[0].TimestampUtc);

            conn.Close();
        }

        private sealed class PersistCheckingEventBus : ITradingEventBus
        {
            private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _factory;
            private readonly Channel<Candle> _candles = Channel.CreateUnbounded<Candle>();

            public PersistCheckingEventBus(IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory)
            {
                _factory = factory;
            }

            public bool SawPersistedCandleBeforePublish { get; private set; }

            public ChannelReader<Candle> CandleReader => _candles.Reader;
            public ChannelReader<PatternCandidate> PatternCandidateReader => Channel.CreateUnbounded<PatternCandidate>().Reader;
            public ChannelReader<AiMarketAnalysis> AiMarketAnalysisReader => Channel.CreateUnbounded<AiMarketAnalysis>().Reader;
            public ChannelReader<TradeSignal> TradeSignalReader => Channel.CreateUnbounded<TradeSignal>().Reader;
            public ChannelReader<OrderRequest> OrderRequestReader => Channel.CreateUnbounded<OrderRequest>().Reader;

            public bool TryPublish(Candle candle)
            {
                using var ctx = _factory.CreateDbContext();
                SawPersistedCandleBeforePublish = ctx.Candles.Any(c =>
                    c.Symbol == candle.Symbol
                    && c.Timeframe == candle.Timeframe
                    && c.TimestampUtc == candle.TimestampUtc);

                return _candles.Writer.TryWrite(candle);
            }

            public ValueTask<bool> PublishAsync(Candle candle, CancellationToken cancellationToken = default)
            {
                return new ValueTask<bool>(TryPublish(candle));
            }

            public bool TryPublish(PatternCandidate pattern) => throw new NotSupportedException();
            public ValueTask<bool> PublishAsync(PatternCandidate pattern, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public bool TryPublish(AiMarketAnalysis analysis) => throw new NotSupportedException();
            public ValueTask<bool> PublishAsync(AiMarketAnalysis analysis, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public bool TryPublish(TradeSignal signal) => throw new NotSupportedException();
            public ValueTask<bool> PublishAsync(TradeSignal signal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public bool TryPublish(OrderRequest order) => throw new NotSupportedException();
            public ValueTask<bool> PublishAsync(OrderRequest order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }
}
