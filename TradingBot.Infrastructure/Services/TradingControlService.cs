using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradingControlService : ITradingControlService
    {
        private readonly ITradingEngineStatusService _statusService;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ILogger<TradingControlService> _logger;

        public TradingControlService(
            ITradingEngineStatusService statusService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<TradingControlService> logger)
        {
            _statusService = statusService;
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task PauseTradingAsync(string reason, CancellationToken cancellationToken = default)
        {
            var current = _statusService.Current;
            _statusService.SetState(
                TradingEngineState.Paused,
                false,
                $"Trading paused: {reason}",
                brokerEnvironmentVerification: current.BrokerEnvironmentVerification,
                connectedAccountId: current.ConnectedAccountId,
                reconciliationCompleted: current.ReconciliationCompleted);
            await PersistControlActionAsync("Pause Trading", reason, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Trading paused: {Reason}", reason);
        }

        public async Task ResumeTradingAsync(string reason, CancellationToken cancellationToken = default)
        {
            var current = _statusService.Current;
            if (current.State != TradingEngineState.Paused || !current.ReconciliationCompleted)
            {
                var rejectionReason = $"Resume rejected from state {current.State}. Reconciliation or operator recovery is required before trading can resume.";
                await PersistControlActionAsync("Resume Trading Rejected", $"{reason}; {rejectionReason}", cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Trading resume rejected from state {State}: {Reason}", current.State, reason);
                return;
            }

            _statusService.SetState(
                TradingEngineState.Ready,
                true,
                $"Trading resumed: {reason}",
                brokerEnvironmentVerification: current.BrokerEnvironmentVerification,
                connectedAccountId: current.ConnectedAccountId,
                reconciliationCompleted: current.ReconciliationCompleted);
            await PersistControlActionAsync("Resume Trading", reason, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Trading resumed: {Reason}", reason);
        }

        public async Task CloseCurrentPositionAsync(string reason, CancellationToken cancellationToken = default)
        {
            _statusService.SetState(TradingEngineState.Paused, false, $"Close current position requested: {reason}");
            await PersistControlActionAsync("Close Current Position", reason, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("Close current position requested: {Reason}", reason);
        }

        public async Task KillSwitchAsync(string reason, CancellationToken cancellationToken = default)
        {
            _statusService.SetState(TradingEngineState.Faulted, false, $"Kill switch activated: {reason}");
            await PersistControlActionAsync("Kill Switch", reason, cancellationToken).ConfigureAwait(false);
            _logger.LogCritical("Kill switch activated: {Reason}", reason);
        }

        private async Task PersistControlActionAsync(string action, string reason, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.BotSessions.Add(new TradingBot.Persistence.BotSession
            {
                StartedUtc = DateTime.UtcNow,
                StoppedUtc = DateTime.UtcNow,
                Notes = $"{action}: {reason}"
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
