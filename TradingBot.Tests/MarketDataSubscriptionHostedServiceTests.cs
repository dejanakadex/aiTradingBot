using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
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
        public async Task StartsCollectionWithoutTradingReadiness()
        {
            var harness = CreateHarness();
            harness.Status.SetState(TradingEngineState.Starting, false, "starting");

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 3);
            await harness.Service.StopAsync(CancellationToken.None);

            Assert.Equal(3, harness.MarketData.Subscriptions.Count);
            harness.Dispose();
        }

        [Fact]
        public async Task FailureOfOneStreamDoesNotStopOtherInstruments()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Instruments =
                [
                    new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY", MarketDataTimeframes = ["1m"] },
                    new InstrumentSettings { InstrumentId = "US-STK-QQQ-SMART", Symbol = "QQQ", MarketDataTimeframes = ["1m"] }
                ]
            });
            harness.MarketData.FailNextSubscriptions("SPY", "1m", 3);

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Any(item => item.Symbol == "QQQ"));

            Assert.DoesNotContain(harness.MarketData.Subscriptions, item => item.Symbol == "SPY");
            Assert.Contains(harness.CollectionStatus.GetAll(), item => item.Symbol == "QQQ" && item.Subscribed);
            await harness.Service.StopAsync(CancellationToken.None);
            harness.Dispose();
        }

        [Fact]
        public async Task CompletedStreamReconnectsAndRequestsAutomaticGapFill()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = ["SPY"],
                MarketDataTimeframes = ["1m"],
                MaximumCandleAgeSeconds = 300
            });

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 1);
            await harness.MarketData.PublishAsync("SPY", "1m", new MarketBar
            {
                Symbol = "SPY",
                Timeframe = "1m",
                TimestampUtc = harness.Clock.UtcNow.AddMinutes(-2),
                ReceivedTimeUtc = harness.Clock.UtcNow,
                Source = "test",
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 100m
            });
            await WaitUntilAsync(() => harness.CollectionStatus.GetAll().Single().LastHeartbeatUtc.HasValue);
            harness.MarketData.CompleteLatest("SPY", "1m");

            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count >= 2, timeoutMilliseconds: 5000);
            await WaitUntilAsync(() => harness.Historical.GapFills.Count == 1);

            var gap = Assert.Single(harness.Historical.GapFills);
            Assert.Equal(harness.Clock.UtcNow.AddMinutes(-1), gap.StartUtc);
            Assert.Equal(harness.Clock.UtcNow, gap.EndUtc);
            var status = Assert.Single(harness.CollectionStatus.GetAll());
            Assert.Equal(1, status.ReconnectCount);
            Assert.Equal(1, status.GapFillCount);
            await harness.Service.StopAsync(CancellationToken.None);
            harness.Dispose();
        }

        [Fact]
        public async Task StaleHeartbeatIsVisibleAndRestartsOnlyThatStream()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = ["SPY"],
                MarketDataTimeframes = ["1m"]
            });

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 1);
            harness.Clock.UtcNow = harness.Clock.UtcNow.AddMinutes(3);

            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count >= 2, timeoutMilliseconds: 5000);

            var status = Assert.Single(harness.CollectionStatus.GetAll());
            Assert.Equal(1, status.ReconnectCount);
            Assert.True(status.ConsecutiveFailures > 0);
            Assert.Contains("heartbeat", status.LastError, StringComparison.OrdinalIgnoreCase);
            await harness.Service.StopAsync(CancellationToken.None);
            harness.Dispose();
        }

        [Fact]
        public async Task StartupRestoresLastCandleAndFillsDowntimeGapBeforeLiveSubscription()
        {
            var harness = CreateHarness(new TradingSettings
            {
                Symbols = ["SPY"],
                MarketDataTimeframes = ["1m"]
            });
            await using (var db = harness.Factory.CreateDbContext())
            {
                db.Candles.Add(new TradingBot.Persistence.Candle
                {
                    InstrumentId = "SPY",
                    Symbol = "SPY",
                    Timeframe = Timeframe.OneMinute,
                    TimestampUtc = harness.Clock.UtcNow.AddMinutes(-2),
                    ReceivedTimeUtc = harness.Clock.UtcNow.AddMinutes(-2),
                    Source = "test",
                    IsFinal = true,
                    QualityStatus = MarketDataQualityStatus.Healthy,
                    Open = 100m,
                    High = 101m,
                    Low = 99m,
                    Close = 100m,
                    Volume = 100m
                });
                await db.SaveChangesAsync();
            }

            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.Historical.GapFills.Count == 1);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 1);

            var gap = Assert.Single(harness.Historical.GapFills);
            Assert.Equal(harness.Clock.UtcNow.AddMinutes(-1), gap.StartUtc);
            Assert.Equal(harness.Clock.UtcNow, gap.EndUtc);
            await harness.Service.StopAsync(CancellationToken.None);
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
        public async Task FakeLiveBarsProduceCandlesAndParallelStrategyPatterns()
        {
            var tradingSettings = new TradingSettings
            {
                Instruments = new[]
                {
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-SPY-SMART",
                        Symbol = "SPY",
                        AllowedDirections = new[] { TradeDirection.Long },
                        StrategyIds = new[] { "hammer-scalp-a", "hammer-scalp-b" },
                        MarketDataTimeframes = new[] { "1m" }
                    }
                },
                MaximumCandleAgeSeconds = 300
            };
            var harness = CreateHarness(tradingSettings, new OpenAiSettings
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
                NullLogger<CandlePatternDetectionBackgroundService>.Instance,
                tradingSettings: Options.Create(tradingSettings));

            await patternService.StartAsync(CancellationToken.None);
            await harness.Service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => harness.MarketData.Subscriptions.Count == 1);

            var at = harness.Clock.UtcNow;
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

            using var patternTimeout = new CancellationTokenSource(3000);
            var patterns = new[]
            {
                await harness.EventBus.PatternCandidateReader.ReadAsync(patternTimeout.Token),
                await harness.EventBus.PatternCandidateReader.ReadAsync(patternTimeout.Token)
            };

            await harness.Service.StopAsync(CancellationToken.None);
            await patternService.StopAsync(CancellationToken.None);

            Assert.All(patterns, pattern => Assert.Equal(PatternType.Hammer, pattern.PatternType));
            Assert.Equal(new[] { "hammer-scalp-a", "hammer-scalp-b" }, patterns.Select(pattern => pattern.StrategyId).Order().ToArray());
            Assert.Equal(2, patterns.Select(pattern => pattern.PatternKey).Distinct().Count());
            await using (var db = harness.Factory.CreateDbContext())
            {
                Assert.True(await db.Candles.AnyAsync(c => c.Symbol == "SPY" && c.Timeframe == Timeframe.OneMinute));
                var persistedPatterns = await db.PatternDetections.Where(p => p.Symbol == "SPY").OrderBy(p => p.StrategyId).ToArrayAsync();
                Assert.Equal(2, persistedPatterns.Length);
                Assert.Equal(new[] { "hammer-scalp-a", "hammer-scalp-b" }, persistedPatterns.Select(pattern => pattern.StrategyId).ToArray());
                Assert.Equal(2, persistedPatterns.Select(pattern => pattern.PatternKey).Distinct().Count());
                Assert.All(persistedPatterns, persistedPattern =>
                {
                    Assert.Equal(PatternType.Hammer, persistedPattern.PatternType);
                    Assert.Equal("US-STK-SPY-SMART", persistedPattern.InstrumentId);
                    Assert.Equal(Timeframe.OneMinute, persistedPattern.Timeframe);
                    Assert.Equal(TradeDirection.Long, persistedPattern.Direction);
                    Assert.NotEqual(Guid.Empty, persistedPattern.SignalId);
                    Assert.NotEmpty(persistedPattern.PatternKey);
                    Assert.StartsWith("patterns-v2+config-", persistedPattern.PatternVersion);
                    Assert.Contains("aboveVwap", persistedPattern.Details);
                    Assert.Contains("confidence", persistedPattern.Details);
                    Assert.Contains("hardConditions", persistedPattern.Details);
                    Assert.Contains("scoreComponents", persistedPattern.Details);
                });
            }

            harness.Dispose();
        }

        private static Harness CreateHarness(TradingSettings? tradingSettings = null, OpenAiSettings? openAiSettings = null)
        {
            var factory = CreateInMemoryFactory(out var connection);
            var eventBus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
            var status = new TradingEngineStatusService();
            var fakeAdapter = new FakeIbkrAdapter();
            var clock = new MutableClock(new DateTime(2026, 9, 24, 15, 0, 0, DateTimeKind.Utc));
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
                new MarketDataValidator(Options.Create(tradingSettings), clock),
                null,
                NullLogger<MarketDataPipeline>.Instance,
                settings: Options.Create(tradingSettings),
                clock: clock);
            var marketData = new FakeSubscriptionMarketDataService();
            var connectionService = new FakeConnectionService(ConnectionStatus.Connected);
            var historical = new FakeHistoricalBackfillService();
            var collectionStatus = new MarketDataCollectionStatusService();
            var service = new MarketDataSubscriptionHostedService(
                marketData,
                pipeline,
                connectionService,
                historical,
                new CandleHistoryService(factory),
                collectionStatus,
                Options.Create(tradingSettings),
                Options.Create(new MarketDataCollectionSettings
                {
                    MonitorIntervalSeconds = 1,
                    ReconnectInitialDelaySeconds = 1,
                    ReconnectMaximumDelaySeconds = 1,
                    HeartbeatGraceSeconds = 0,
                    MaximumGapFillLookbackMinutes = 60
                }),
                clock,
                NullLogger<MarketDataSubscriptionHostedService>.Instance);

            return new Harness(service, marketData, eventBus, status, connectionService, historical, collectionStatus, clock, factory, connection);
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

        private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
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
            FakeHistoricalBackfillService Historical,
            MarketDataCollectionStatusService CollectionStatus,
            MutableClock Clock,
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
            private readonly System.Collections.Concurrent.ConcurrentQueue<FakeSubscription> _subscriptions = new();
            private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<FakeSubscription> Subscriptions => _subscriptions.ToArray();
            public int HistoricalRequests { get; private set; }

            public Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(HistoricalBarRequest request, CancellationToken cancellationToken = default)
            {
                HistoricalRequests++;
                return Task.FromResult<IEnumerable<MarketBar>>(Array.Empty<MarketBar>());
            }

            public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
            {
                var key = $"{symbol}|{timeframe}";
                if (_failures.TryGetValue(key, out var remaining) && remaining > 0)
                {
                    _failures[key] = remaining - 1;
                    throw new InvalidOperationException($"Simulated subscription failure for {key}.");
                }
                var subscription = new FakeSubscription(symbol, timeframe);
                _subscriptions.Enqueue(subscription);
                return Task.FromResult<IMarketDataSubscription>(subscription);
            }

            public void FailNextSubscriptions(string symbol, string timeframe, int count) => _failures[$"{symbol}|{timeframe}"] = count;

            public void CompleteLatest(string symbol, string timeframe) => Subscriptions
                .Last(item => item.Symbol == symbol && item.Timeframe == timeframe)
                .Channel.Writer.TryComplete();

            public async Task PublishAsync(string symbol, string timeframe, MarketBar bar)
            {
                var subscription = Subscriptions.Last(s => s.Symbol == symbol && s.Timeframe == timeframe && !s.Disposed);
                await subscription.Channel.Writer.WriteAsync(bar);
            }
        }

        private sealed class FakeHistoricalBackfillService : IHistoricalBackfillService
        {
            public System.Collections.Concurrent.ConcurrentQueue<HistoricalGapFillResult> GapFills { get; } = new();

            public Task SynchronizePlanAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task<HistoricalBackfillJobSnapshot?> RunNextSegmentAsync(CancellationToken cancellationToken = default) => Task.FromResult<HistoricalBackfillJobSnapshot?>(null);
            public Task<IReadOnlyList<HistoricalBackfillJobSnapshot>> GetJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalBackfillJobSnapshot>>(Array.Empty<HistoricalBackfillJobSnapshot>());
            public Task<IReadOnlyList<HistoricalDataGapSnapshot>> GetGapsAsync(string? instrumentId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<HistoricalDataGapSnapshot>>(Array.Empty<HistoricalDataGapSnapshot>());

            public Task<HistoricalGapFillResult> FillGapAsync(
                string instrumentId,
                string symbol,
                string timeframe,
                DateTime startUtc,
                DateTime endUtc,
                CancellationToken cancellationToken = default)
            {
                var result = new HistoricalGapFillResult(true, instrumentId, timeframe, startUtc, endUtc, 2, 2, 0, string.Empty);
                GapFills.Enqueue(result);
                return Task.FromResult(result);
            }
        }

        private sealed class MutableClock : IClock
        {
            public MutableClock(DateTime utcNow) => UtcNow = utcNow;
            public DateTime UtcNow { get; set; }
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
