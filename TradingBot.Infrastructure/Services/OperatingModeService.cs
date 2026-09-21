using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class OperatingModeService : IOperatingModeService
    {
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ILogger<OperatingModeService> _logger;
        private readonly TradingSettings _tradingSettings;
        private readonly object _sync = new();
        private TradingOperatingMode _currentMode;

        public OperatingModeService(
            IOptions<TradingSettings> tradingSettings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<OperatingModeService> logger)
        {
            _tradingSettings = tradingSettings.Value;
            _currentMode = tradingSettings.Value.OperatingMode == TradingOperatingMode.LiveTrading && !tradingSettings.Value.LiveTradingExplicitlyEnabled
                ? TradingOperatingMode.AnalysisOnly
                : tradingSettings.Value.OperatingMode;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public TradingOperatingMode CurrentMode
        {
            get
            {
                lock (_sync)
                {
                    return _currentMode;
                }
            }
        }

        public async Task SetModeAsync(TradingOperatingMode mode, string reason, CancellationToken cancellationToken = default)
        {
            if (mode == TradingOperatingMode.LiveTrading && !_tradingSettings.LiveTradingExplicitlyEnabled)
            {
                _logger.LogError("Rejected operating mode change to LiveTrading because LiveTradingExplicitlyEnabled is false: {Reason}", reason);
                await PersistModeChangeAsync($"Operating mode change to LiveTrading rejected: {reason}", cancellationToken).ConfigureAwait(false);
                return;
            }

            lock (_sync)
            {
                _currentMode = mode;
            }

            _logger.LogWarning("Trading operating mode changed to {Mode}: {Reason}", mode, reason);
            await PersistModeChangeAsync($"Operating mode changed to {mode}: {reason}", cancellationToken).ConfigureAwait(false);
        }

        private async Task PersistModeChangeAsync(string note, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.BotSessions.Add(new TradingBot.Persistence.BotSession
            {
                StartedUtc = DateTime.UtcNow,
                StoppedUtc = DateTime.UtcNow,
                Notes = note
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
