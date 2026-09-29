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
        private readonly IExitManagementService? _exitManagementService;
        private readonly IOrderManager? _orderManager;

        public TradingControlService(
            ITradingEngineStatusService statusService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<TradingControlService> logger,
            IExitManagementService? exitManagementService = null,
            IOrderManager? orderManager = null)
        {
            _statusService = statusService;
            _dbFactory = dbFactory;
            _logger = logger;
            _exitManagementService = exitManagementService;
            _orderManager = orderManager;
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
            await PersistPermissionAsync(TradingPermissionState.Paused, reason, cancellationToken).ConfigureAwait(false);
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
            await PersistPermissionAsync(TradingPermissionState.Active, reason, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Trading resumed: {Reason}", reason);
        }

        public async Task CloseCurrentPositionAsync(string reason, CancellationToken cancellationToken = default)
        {
            _statusService.SetState(TradingEngineState.Paused, false, $"Close current position requested: {reason}");
            await PersistPermissionAsync(TradingPermissionState.Paused, reason, cancellationToken).ConfigureAwait(false);
            await PersistControlActionAsync("Close Current Position", reason, cancellationToken).ConfigureAwait(false);
            if (_exitManagementService != null)
            {
                await _exitManagementService.RequestCloseAllAsync($"OperatorClose:{reason}", cancellationToken).ConfigureAwait(false);
            }
            _logger.LogWarning("Close current position requested: {Reason}", reason);
        }

        public async Task KillSwitchAsync(string reason, CancellationToken cancellationToken = default)
        {
            _statusService.SetState(TradingEngineState.Faulted, false, $"Kill switch activated: {reason}");
            await PersistPermissionAsync(TradingPermissionState.KillSwitch, reason, cancellationToken).ConfigureAwait(false);
            await PersistControlActionAsync("Kill Switch", reason, cancellationToken).ConfigureAwait(false);
            if (_orderManager != null)
            {
                await CancelAllOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
            }
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

        private async Task PersistPermissionAsync(TradingPermissionState state, string reason, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.TradingControlStateRecords.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
            if (record == null)
            {
                record = new TradingBot.Persistence.TradingControlStateRecord { Id = 1 };
                db.TradingControlStateRecords.Add(record);
            }

            record.State = state;
            record.Reason = reason;
            record.UpdatedUtc = DateTime.UtcNow;
            record.Version++;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task CancelAllOpenOrdersAsync(CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var ids = await db.OrderRecords.AsNoTracking()
                .Where(x => x.BrokerOrderId != string.Empty
                    && (x.Status == OrderStatus.New
                        || x.Status == OrderStatus.Submitted
                        || x.Status == OrderStatus.PartiallyFilled
                        || x.Status == OrderStatus.Unknown
                        || x.Status == OrderStatus.PendingBrokerConfirmation
                        || x.Status == OrderStatus.CancelPending))
                .Select(x => x.BrokerOrderId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var id in ids)
            {
                await _orderManager!.CancelOrderAsync(id, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
