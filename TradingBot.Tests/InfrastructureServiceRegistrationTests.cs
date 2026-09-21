using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests
{
    public sealed class InfrastructureServiceRegistrationTests
    {
        [Fact]
        public void AddInfrastructure_WithoutConcreteIbkrAdapter_ValidatesAndFailsClosed()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TradingSettings>(_ => { });
            services.Configure<RiskSettings>(_ => { });
            services.Configure<OpenAiSettings>(_ => { });
            services.Configure<IbkrSettings>(_ => { });
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite("Data Source=:memory:"));
            services.AddSingleton<IIbkrAdapter, UnavailableIbkrAdapter>();
            services.AddSingleton<IIbkrConnectionService, UnavailableIbkrConnectionService>();
            services.AddSingleton<IAccountService, UnavailableAccountService>();
            services.AddSingleton<IPositionService, UnavailablePositionService>();

            services.AddInfrastructure();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            Assert.IsType<UnavailableIbkrAdapter>(provider.GetRequiredService<IIbkrAdapter>());
            var connection = Assert.IsType<UnavailableIbkrConnectionService>(provider.GetRequiredService<IIbkrConnectionService>());
            Assert.Equal(ConnectionStatus.Disconnected, connection.Status);
            Assert.IsType<UnavailableAccountService>(provider.GetRequiredService<IAccountService>());
            Assert.IsType<UnavailablePositionService>(provider.GetRequiredService<IPositionService>());
            Assert.NotNull(provider.GetRequiredService<MarketDataPipeline>());
            Assert.NotNull(provider.GetRequiredService<IBrokerStateReconciliationService>());
        }

        [Fact]
        public void AddInfrastructure_WhenIbkrApiIsAvailable_RegistersSharedRealBrokerService()
        {
            var realBrokerType = Type.GetType("TradingBot.Infrastructure.Services.IbkrBrokerService, TradingBot.Infrastructure");
            if (realBrokerType == null)
            {
                return;
            }

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TradingSettings>(_ => { });
            services.Configure<RiskSettings>(_ => { });
            services.Configure<OpenAiSettings>(_ => { });
            services.Configure<IbkrSettings>(_ => { });
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite("Data Source=:memory:"));

            services.AddInfrastructure();

            using var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            var adapter = provider.GetRequiredService<IIbkrAdapter>();
            var execution = provider.GetRequiredService<IOrderExecutionService>();
            var account = provider.GetRequiredService<IAccountService>();
            var positions = provider.GetRequiredService<IPositionService>();
            var marketData = provider.GetRequiredService<IMarketDataService>();

            Assert.Equal(realBrokerType, adapter.GetType());
            Assert.Same(adapter, execution);
            Assert.Same(adapter, account);
            Assert.Same(adapter, positions);
            Assert.Same(adapter, marketData);
            Assert.IsType<IbkrConnectionService>(provider.GetRequiredService<IIbkrConnectionService>());
        }

        [Fact]
        public async Task DashboardService_WithUnavailableIbkrRegistration_ShowsNotConfiguredInsteadOfFailed()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TradingSettings>(_ => { });
            services.Configure<RiskSettings>(_ => { });
            services.Configure<OpenAiSettings>(_ => { });
            services.Configure<IbkrSettings>(_ => { });
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IIbkrAdapter, UnavailableIbkrAdapter>();
            services.AddSingleton<IIbkrConnectionService, UnavailableIbkrConnectionService>();
            services.AddSingleton<IAccountService, UnavailableAccountService>();
            services.AddSingleton<IPositionService, UnavailablePositionService>();

            services.AddInfrastructure();

            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            var status = provider.GetRequiredService<ITradingEngineStatusService>();
            await provider.GetRequiredService<IIbkrConnectionService>().ConnectAsync();

            var snapshot = await provider.GetRequiredService<IDashboardService>().GetSnapshotAsync();

            Assert.Equal("Unavailable - IBKR adapter not registered", snapshot.IbkrConnectionState);
            Assert.False(status.Current.TradingEnabled);
        }

        [Fact]
        public async Task DashboardService_ShowsAiPipelineGateAndAnalyzerDecisionCounts()
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TradingSettings>(_ => { });
            services.Configure<RiskSettings>(_ => { });
            services.Configure<OpenAiSettings>(_ => { });
            services.Configure<IbkrSettings>(_ => { });
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IIbkrAdapter, UnavailableIbkrAdapter>();
            services.AddSingleton<IIbkrConnectionService, UnavailableIbkrConnectionService>();
            services.AddSingleton<IAccountService, UnavailableAccountService>();
            services.AddSingleton<IPositionService, UnavailablePositionService>();
            services.AddInfrastructure();

            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.AiAnalysisRecords.AddRange(
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        AgentType = AiAgentType.Pipeline.ToString(),
                        Context = "PatternRejectedBeforeAi",
                        ResultJson = """{"recordType":"PatternRejectedBeforeAi","symbol":"SPY","pattern":"Hammer","patternTimeframe":"OneMinute","patternDetectedAtUtc":"2026-08-27T14:42:00Z","deterministicPatternQuality":0.5484,"reason":"Pattern final quality 0.5484 is below trade setup threshold 0.6."}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        AgentType = AiAgentType.Pipeline.ToString(),
                        Context = "PatternSentToAi",
                        ResultJson = """{"recordType":"PatternSentToAi","reason":"Deterministic pattern quality met AI threshold."}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = DateTime.UtcNow.AddSeconds(2),
                        AgentType = AiAgentType.Pipeline.ToString(),
                        Context = "CriticSkipped",
                        ResultJson = """{"recordType":"CriticSkipped","reason":"Analyzer action was WAIT; critic only runs for BUY."}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        AgentType = AiAgentType.Analyzer.ToString(),
                        ResultJson = """{"result":{"action":"WAIT","confidence":0.7}}"""
                    },
                    new TradingBot.Persistence.AiAnalysisRecord
                    {
                        TimestampUtc = DateTime.UtcNow,
                        AgentType = AiAgentType.Analyzer.ToString(),
                        ResultJson = """{"result":{"action":"REJECT","confidence":0.8}}"""
                    });
                await db.SaveChangesAsync();
            }

            var snapshot = await provider.GetRequiredService<IDashboardService>().GetSnapshotAsync();

            Assert.Equal(1, snapshot.PatternsRejectedBeforeAiToday);
            Assert.Equal(1, snapshot.PatternsSentToAiToday);
            var gateRejection = Assert.Single(snapshot.RecentPatternGateRejections);
            Assert.Equal("SPY", gateRejection.Symbol);
            Assert.Equal("Hammer", gateRejection.Pattern);
            Assert.Equal("OneMinute", gateRejection.Timeframe);
            Assert.Equal(0.5484m, gateRejection.Quality);
            Assert.Contains("below trade setup threshold", gateRejection.Reason);
            Assert.Equal(1, snapshot.CriticSkippedToday);
            Assert.Equal(0, snapshot.AnalyzerBuyToday);
            Assert.Equal(1, snapshot.AnalyzerWaitToday);
            Assert.Equal(1, snapshot.AnalyzerRejectToday);
            Assert.Equal("CriticSkipped", snapshot.LatestPipelineDecision);
            Assert.Contains("critic only runs for BUY", snapshot.LatestPipelineDecisionReason);
        }

        [Fact]
        public async Task DashboardService_AfterRegularMarketClose_ReportsMarketClosedInsteadOfStale()
        {
            var nowUtc = new DateTime(2026, 8, 24, 20, 57, 0, DateTimeKind.Utc);
            var latestCandleUtc = new DateTime(2026, 8, 24, 19, 59, 0, DateTimeKind.Utc);
            var snapshot = await GetDashboardSnapshotWithLatestCandleAsync(nowUtc, latestCandleUtc);

            Assert.StartsWith("Market closed", snapshot.MarketDataStatus);
            Assert.Contains("58m", snapshot.MarketDataStatus);
        }

        [Fact]
        public async Task DashboardService_DuringRegularMarketHours_ReportsStaleWhenLatestCandleIsTooOld()
        {
            var nowUtc = new DateTime(2026, 8, 24, 15, 0, 0, DateTimeKind.Utc);
            var latestCandleUtc = new DateTime(2026, 8, 24, 14, 50, 0, DateTimeKind.Utc);
            var snapshot = await GetDashboardSnapshotWithLatestCandleAsync(nowUtc, latestCandleUtc);

            Assert.StartsWith("Stale", snapshot.MarketDataStatus);
            Assert.Contains("10m", snapshot.MarketDataStatus);
        }

        private static async Task<TradingBot.Domain.Models.DashboardSnapshot> GetDashboardSnapshotWithLatestCandleAsync(
            DateTime nowUtc,
            DateTime latestCandleUtc)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddOptions();
            services.Configure<TradingSettings>(settings =>
            {
                settings.Enabled = true;
                settings.MaximumCandleAgeSeconds = 300;
                settings.TradingStartHourNewYork = 9;
                settings.TradingStartMinuteNewYork = 30;
                settings.TradingEndHourNewYork = 16;
                settings.TradingEndMinuteNewYork = 0;
            });
            services.Configure<RiskSettings>(_ => { });
            services.Configure<OpenAiSettings>(_ => { });
            services.Configure<IbkrSettings>(_ => { });
            services.AddDbContextFactory<TradingBotDbContext>(options => options.UseSqlite(connection));
            services.AddSingleton<IIbkrAdapter, UnavailableIbkrAdapter>();
            services.AddSingleton<IIbkrConnectionService, UnavailableIbkrConnectionService>();
            services.AddSingleton<IAccountService, UnavailableAccountService>();
            services.AddSingleton<IPositionService, UnavailablePositionService>();
            services.AddInfrastructure();
            services.AddSingleton<IClock>(new FixedClock(nowUtc));

            using var provider = services.BuildServiceProvider();
            await using (var db = await provider.GetRequiredService<IDbContextFactory<TradingBotDbContext>>().CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
                db.Candles.Add(new TradingBot.Persistence.Candle
                {
                    Symbol = "SPY",
                    Timeframe = Timeframe.OneMinute,
                    TimestampUtc = latestCandleUtc,
                    Open = 100m,
                    High = 101m,
                    Low = 99m,
                    Close = 100.5m,
                    Volume = 1000m
                });
                await db.SaveChangesAsync();
            }

            return await provider.GetRequiredService<IDashboardService>().GetSnapshotAsync();
        }

        private sealed class FixedClock : IClock
        {
            public FixedClock(DateTime utcNow)
            {
                UtcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            }

            public DateTime UtcNow { get; }
        }
    }
}
