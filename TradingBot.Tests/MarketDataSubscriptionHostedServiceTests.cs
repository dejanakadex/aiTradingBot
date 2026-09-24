using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests
{
    public sealed class MarketDataSubscriptionHostedServiceTests
    {
        [Fact]
        public async Task StartsConfiguredSubscriptionsWhenEngineIsReady()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = new[] { "SPY" },
                MarketDataTimeframes = new[] { "1m", "5m", "15m" }
            });
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Contains(("SPY", "1m"), harness.MarketData.Subscriptions.Select(s => (s.Symbol, s.Timeframe)));
            Assert.Contains(("SPY", "5m"), harness.MarketData.Subscriptions.Select(s => (s.Symbol, s.Timeframe)));
            Assert.Contains(("SPY", "15m"), harness.MarketData.Subscriptions.Select(s => (s.Symbol, s.Timeframe)));
            harness.Dispose();
        }

        [Fact]
        public async Task StartsPerInstrumentSubscriptionsForAllEnabledInstruments()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Instruments = new[]
                {
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-SPY-SMART",
                        Symbol = "SPY",
                        MarketDataTimeframes = new[] { "1m", "5m" }
                    },
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-QQQ-SMART",
                        Symbol = "QQQ",
                        MarketDataTimeframes = new[] { "1m" }
                    },
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-AAPL-SMART",
                        Symbol = "AAPL",
                        Enabled = false
                    }
                }
            });
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            var subscriptions = harness.MarketData.Subscriptions.Select(item => (item.Symbol, item.Timeframe)).ToArray();
            Assert.Contains(("SPY", "1m"), subscriptions);
            Assert.Contains(("SPY", "5m"), subscriptions);
            Assert.Contains(("QQQ", "1m"), subscriptions);
            Assert.DoesNotContain(subscriptions, item => item.Symbol == "AAPL");
            harness.Dispose();
        }

        [Fact]
        public async Task DoesNotSubscribeBeforeEngineIsReady()
        {
            var harness = CreateHarness();
            harness.Status.SetState(TradingEngineState.Starting, false, "starting");

            await harness.Service.StartAsync(CancellationToken.None);
            await Task.Delay(1200);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Empty(harness.MarketData.Subscriptions);
            harness.Dispose();
        }

        [Fact]
        public async Task DoesNotSubscribeBeforeIbkrIsConnected()
        {
            var harness = CreateHarness();
            harness.Connection.SetStatus(ConnectionStatus.Disconnected);
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);

            await harness.Service.StartAsync(CancellationToken.None);
            await Task.Delay(1200);

            Assert.Empty(harness.MarketData.Subscriptions);

            harness.Connection.SetStatus(ConnectionStatus.Connected);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            harness.Dispose();
        }

        [Fact]
        public async Task DisposesSubscriptionsOnShutdown()
        {
            var harness = CreateHarness();
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.All(harness.MarketData.Subscriptions, subscription => Assert.True(subscription.Disposed));
            harness.Dispose();
        }

        [Fact]
        public async Task StartsLiveSubscriptionsWithoutWaitingForHistoricalBackfill()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = new[] { "SPY" },
                MarketDataTimeframes = new[] { "1m", "5m", "15m" }
            }, new OpenAiSettings
            {
                MarketContext = new OpenAiMarketContextSettings
                {
                    OneMinuteCandles = 2,
                    FiveMinuteCandles = 2,
                    FifteenMinuteCandles = 2
                }
            });
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Equal(0, harness.MarketData.HistoricalRequests);
            Assert.False(harness.EventBus.CandleReader.TryRead(out _));

            harness.Dispose();
        }

        [Fact]
        public async Task FakeLiveBarsProduceCandlesAndPatternProcessingRuns()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = new[] { "SPY" },
                MarketDataTimeframes = new[] { "1m" },
                MaximumCandleAgeSeconds = 300
            }, new OpenAiSettings
            {
                MarketContext = new OpenAiMarketContextSettings
                {
                    OneMinuteCandles = 2,
                    FiveMinuteCandles = 2,
                    FifteenMinuteCandles = 2
                }
            });
            harness.Status.SetState(TradingEngineState.Ready, true, "ready", reconciliationCompleted: true);
            var patternService = new CandlePatternDetectionBackgroundService(
                harness.EventBus,
                new CandleHistoryService(harness.Factory),
                new FeatureEngine(),
                new PatternDetector(new PatternDetectorOptions(), NullLogger<PatternDetector>.Instance),
                harness.Status,
                harness.Factory,
                NullLogger<CandlePatternDetectionBackgroundService>.Instance);

            await patternService.StartAsync(CancellationToken.None);
            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 1);

            var at = DateTime.UtcNow;
            await harness.MarketData.PublishAsync("SPY", "1m", new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = at.AddMinutes(-1),
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 900m
            });
            await harness.MarketData.PublishAsync("SPY", "1m", new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = at,
                Open = 100m,
                High = 101m,
                Low = 90m,
                Close = 100.5m,
                Volume = 1000m
            });

            var pattern = await harness.EventBus.PatternCandidateReader.ReadAsync(new CancellationTokenSource(3000).Token);

            await harness.Service.StopAsync(CancellationToken.None);
            await patternService.StopAsync(CancellationToken.None);

            Assert.Equal(PatternType.Hammer, pattern.PatternType);
            await using (var db = harness.Factory.CreateDbContext())
            {
                Assert.True(await db.Candles.AnyAsync(c => c.Symbol == "SPY" && c.Timeframe == Timeframe.OneMinute));
                var persistedPattern = await db.PatternDetections.SingleAsync(p => p.Symbol == "SPY");
                Assert.Equal(PatternType.Hammer, persistedPattern.PatternType);
                Assert.Contains("aboveVwap", persistedPattern.Details);
                Assert.Contains("confidence", persistedPattern.Details);
            }

            harness.Dispose();
        }

        private static Harness CreateHarness(TradingSettings? tradingSettings = null, OpenAiSettings? openAiSettings = null)
        {
            var factory = CreateInMemoryFactory(out var connection);
            var eventBus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
            var status = new TradingEngineStatusService();
            var fakeAdapter = new FakeIbkrAdapter();
            tradingSettings ??= new TradingSettings
            {
                Enabled = true,
                Symbols = new[] { "SPY" },
                MarketDataTimeframes = new[] { "1m", "5m", "15m" },
                MaximumCandleAgeSeconds = 300
            };
            tradingSettings.Enabled = true;
            openAiSettings ??= new OpenAiSettings
            {
                MarketContext = new OpenAiMarketContextSettings
                {
                    OneMinuteCandles = 2,
                    FiveMinuteCandles = 2,
                    FifteenMinuteCandles = 2
                }
            };

            var pipeline = new MarketDataPipeline(
                fakeAdapter,
                eventBus,
                factory,
                new MarketDataValidator(Options.Create(tradingSettings), new SystemClock()),
                null,
                NullLogger<MarketDataPipeline>.Instance,
                settings: Options.Create(tradingSettings));
            var marketData = new FakeSubscriptionMarketDataService();
            var connectionService = new FakeConnectionService(ConnectionStatus.Connected);
            var service = new MarketDataSubscriptionHostedService(
                marketData,
                pipeline,
                status,
                connectionService,
                Options.Create(tradingSettings),
                NullLogger<MarketDataSubscriptionHostedService>.Instance);

            return new Harness(service, marketData, eventBus, status, connectionService, factory, connection);
        }

        private static IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection connection)
        {
            var connectionString = $"Data Source=market-data-tests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            connection = new SqliteConnection(connectionString);
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>()
                .UseSqlite(connectionString)
                .Options;

            var factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return factory;
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(3000);
            while (!condition())
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        private static async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            using var timeout = new CancellationTokenSource(3000);
            while (!await condition())
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        private sealed record Harness(
            MarketDataSubscriptionHostedService Service,
            FakeSubscriptionMarketDataService MarketData,
            TradingEventBus EventBus,
            TradingEngineStatusService Status,
            FakeConnectionService Connection,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> Factory,
            SqliteConnection DatabaseConnection) : IDisposable
        {
            public void Dispose()
            {
                EventBus.Dispose();
                DatabaseConnection.Dispose();
            }
        }

        private sealed class FakeConnectionService : IIbkrConnectionService
        {
            public FakeConnectionService(ConnectionStatus status)
            {
                Status = status;
            }

            public ConnectionStatus Status { get; private set; }

            public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                SetStatus(ConnectionStatus.Connected);
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                SetStatus(ConnectionStatus.Disconnected);
                return Task.CompletedTask;
            }

            public void SetStatus(ConnectionStatus status)
            {
                Status = status;
                _ = ConnectionStatusChanged?.Invoke(status);
            }
        }

        private sealed class FakeSubscriptionMarketDataService : IMarketDataService
        {
            public List<FakeSubscription> Subscriptions { get; } = new();
            public int HistoricalRequests { get; private set; }

            public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default)
            {
                HistoricalRequests++;
                return Task.FromResult<IEnumerable<MarketBar>>(Array.Empty<MarketBar>());
            }

            public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
            {
                var subscription = new FakeSubscription(symbol, timeframe);
                Subscriptions.Add(subscription);
                return Task.FromResult<IMarketDataSubscription>(subscription);
            }

            public async Task PublishAsync(string symbol, string timeframe, MarketBar bar)
            {
                var subscription = Subscriptions.Single(s => s.Symbol == symbol && s.Timeframe == timeframe);
                await subscription.Channel.Writer.WriteAsync(bar);
            }
        }

        private sealed class FakeSubscription : IMarketDataSubscription
        {
            public FakeSubscription(string symbol, string timeframe)
            {
                Symbol = symbol;
                Timeframe = timeframe;
                Channel = System.Threading.Channels.Channel.CreateUnbounded<MarketBar>();
            }

            public string Symbol { get; }
            public string Timeframe { get; }
            public Channel<MarketBar> Channel { get; }
            public bool Disposed { get; private set; }
            public ChannelReader<MarketBar> Reader => Channel.Reader;

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                Channel.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBot.Persistence.TradingBotDbContext CreateDbContext()
            {
                return new TradingBot.Persistence.TradingBotDbContext(_options);
            }

            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBot.Persistence.TradingBotDbContext>(CreateDbContext());
            }
        }
    }
}
