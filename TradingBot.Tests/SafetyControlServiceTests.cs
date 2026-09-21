using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class SafetyControlServiceTests
    {
        [Fact]
        public async Task ResumeTradingAsync_DoesNotReviveFaultedEngine()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            status.SetState(TradingEngineState.Faulted, false, "Kill switch active.");
            var service = new TradingControlService(status, factory, NullLogger<TradingControlService>.Instance);

            await service.ResumeTradingAsync("operator clicked resume");

            Assert.Equal(TradingEngineState.Faulted, status.Current.State);
            Assert.False(status.Current.TradingEnabled);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Contains(await db.BotSessions.ToListAsync(), session => session.Notes.Contains("Resume Trading Rejected"));
            connection.Dispose();
        }

        [Fact]
        public async Task PauseAndResumeTradingAsync_PreservesReconciliationState()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var status = new TradingEngineStatusService();
            status.SetState(
                TradingEngineState.Ready,
                true,
                "ready",
                brokerEnvironmentVerification: BrokerEnvironmentVerificationStatus.VerifiedPaper,
                connectedAccountId: "DU123",
                reconciliationCompleted: true);
            var service = new TradingControlService(status, factory, NullLogger<TradingControlService>.Instance);

            await service.PauseTradingAsync("test pause");
            Assert.Equal(TradingEngineState.Paused, status.Current.State);
            Assert.True(status.Current.ReconciliationCompleted);
            Assert.Equal(BrokerEnvironmentVerificationStatus.VerifiedPaper, status.Current.BrokerEnvironmentVerification);

            await service.ResumeTradingAsync("test resume");

            Assert.Equal(TradingEngineState.Ready, status.Current.State);
            Assert.True(status.Current.TradingEnabled);
            Assert.True(status.Current.ReconciliationCompleted);
            Assert.Equal("DU123", status.Current.ConnectedAccountId);

            connection.Dispose();
        }

        [Fact]
        public async Task SetModeAsync_RejectsLiveTradingWhenExplicitFlagIsFalse()
        {
            var factory = CreateInMemoryFactory(out var connection);
            var service = new OperatingModeService(
                Options.Create(new TradingSettings
                {
                    OperatingMode = TradingOperatingMode.AnalysisOnly,
                    LiveTradingExplicitlyEnabled = false
                }),
                factory,
                NullLogger<OperatingModeService>.Instance);

            await service.SetModeAsync(TradingOperatingMode.LiveTrading, "test request");

            Assert.Equal(TradingOperatingMode.AnalysisOnly, service.CurrentMode);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Contains(await db.BotSessions.ToListAsync(), session => session.Notes.Contains("LiveTrading rejected"));
            connection.Dispose();
        }

        private static IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection connection)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>()
                .UseSqlite(connection)
                .Options;

            var factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return factory;
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
