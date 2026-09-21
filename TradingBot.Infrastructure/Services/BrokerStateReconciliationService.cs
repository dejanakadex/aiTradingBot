using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class BrokerStateReconciliationService : IBrokerStateReconciliationService
    {
        private readonly IIbkrConnectionService _connectionService;
        private readonly IAccountService _accountService;
        private readonly IPositionService _positionService;
        private readonly IOrderExecutionService _orderExecutionService;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IbkrSettings _ibkrSettings;
        private readonly TradingSettings _tradingSettings;
        private readonly ILogger<BrokerStateReconciliationService> _logger;

        public BrokerStateReconciliationService(
            IIbkrConnectionService connectionService,
            IAccountService accountService,
            IPositionService positionService,
            IOrderExecutionService orderExecutionService,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ITradingEngineStatusService statusService,
            IOptions<IbkrSettings> ibkrSettings,
            IOptions<TradingSettings> tradingSettings,
            ILogger<BrokerStateReconciliationService> logger)
        {
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
            _positionService = positionService ?? throw new ArgumentNullException(nameof(positionService));
            _orderExecutionService = orderExecutionService ?? throw new ArgumentNullException(nameof(orderExecutionService));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _ibkrSettings = ibkrSettings?.Value ?? new IbkrSettings();
            _tradingSettings = tradingSettings?.Value ?? new TradingSettings();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<BrokerReconciliationStatus> ReconcileAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _statusService.SetState(TradingEngineState.Connecting, false, "Connecting to IBKR.");
                await _connectionService.ConnectAsync(cancellationToken).ConfigureAwait(false);

                if (_connectionService.Status != ConnectionStatus.Connected)
                {
                    return SetFinal(TradingEngineState.Degraded, "IBKR connection did not reach Connected state.", new[] { $"Connection status: {_connectionService.Status}" });
                }

                _statusService.SetState(TradingEngineState.Reconciling, false, "Reading broker and local state.");

                var expectedAccountId = ExpectedAccountId();
                var account = await _accountService.GetAccountInfoAsync(expectedAccountId, cancellationToken).ConfigureAwait(false);
                var environment = VerifyEnvironment(account.AccountId, out var verificationError);
                if (!string.IsNullOrWhiteSpace(verificationError))
                {
                    _logger.LogError("IBKR environment verification failed: {Reason}", verificationError);
                    return SetFinal(TradingEngineState.Degraded, "IBKR environment/account verification failed. Trading remains disabled.", new[] { verificationError }, environment, account.AccountId);
                }

                var accountId = account.AccountId;
                var brokerPositions = (await _positionService.GetPositionsAsync(accountId, cancellationToken).ConfigureAwait(false)).ToList();
                var brokerOrders = (await _orderExecutionService.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false)).ToList();

                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var localOpenTrades = await db.Trades.AsNoTracking()
                    .Where(t => !t.ClosedUtc.HasValue && t.Size != 0m)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var localOpenOrders = await db.OrderRecords.AsNoTracking()
                    .Where(o => o.Status == OrderStatus.New
                        || o.Status == OrderStatus.PendingBrokerConfirmation
                        || o.Status == OrderStatus.Unknown
                        || o.Status == OrderStatus.Submitted
                        || o.Status == OrderStatus.PartiallyFilled)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var mismatches = new List<string>();
                ComparePositions(brokerPositions, localOpenTrades, mismatches);
                CompareOpenOrders(brokerOrders, localOpenOrders, mismatches);

                if (mismatches.Count > 0)
                {
                    foreach (var mismatch in mismatches)
                    {
                        _logger.LogWarning("Broker reconciliation mismatch: {Mismatch}", mismatch);
                    }

                    return SetFinal(TradingEngineState.Degraded, "Broker state does not match SQLite. Trading remains disabled.", mismatches);
                }

                return SetFinal(TradingEngineState.Ready, "Broker state reconciled. Trading engine ready.", Array.Empty<string>(), environment, account.AccountId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Broker reconciliation canceled during shutdown.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Broker reconciliation failed. Trading remains disabled.");
                return SetFinal(TradingEngineState.Faulted, "Broker reconciliation failed. Trading remains disabled.", new[] { ex.Message });
            }
        }

        private BrokerReconciliationStatus SetFinal(
            TradingEngineState state,
            string message,
            IReadOnlyList<string> mismatches,
            BrokerEnvironmentVerificationStatus environment = BrokerEnvironmentVerificationStatus.Unknown,
            string? connectedAccountId = null)
        {
            _statusService.SetState(
                state,
                state == TradingEngineState.Ready,
                message,
                mismatches,
                environment,
                connectedAccountId,
                reconciliationCompleted: state == TradingEngineState.Ready);
            return _statusService.Current;
        }

        private string ExpectedAccountId()
        {
            if (_tradingSettings.OperatingMode == TradingOperatingMode.PaperTrading)
            {
                return _ibkrSettings.PaperAccountId ?? string.Empty;
            }

            if (_tradingSettings.OperatingMode == TradingOperatingMode.AnalysisOnly
                && !string.IsNullOrWhiteSpace(_ibkrSettings.PaperAccountId))
            {
                return _ibkrSettings.PaperAccountId;
            }

            return _ibkrSettings.AccountId ?? string.Empty;
        }

        private BrokerEnvironmentVerificationStatus VerifyEnvironment(string connectedAccountId, out string? error)
        {
            error = null;

            if (_tradingSettings.OperatingMode == TradingOperatingMode.AnalysisOnly)
            {
                if (!string.IsNullOrWhiteSpace(_ibkrSettings.PaperAccountId)
                    && string.Equals(connectedAccountId, _ibkrSettings.PaperAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    return BrokerEnvironmentVerificationStatus.VerifiedPaper;
                }

                if (!string.IsNullOrWhiteSpace(_ibkrSettings.AccountId)
                    && string.Equals(connectedAccountId, _ibkrSettings.AccountId, StringComparison.OrdinalIgnoreCase))
                {
                    return BrokerEnvironmentVerificationStatus.VerifiedLive;
                }

                return BrokerEnvironmentVerificationStatus.Unknown;
            }

            if (_tradingSettings.OperatingMode == TradingOperatingMode.PaperTrading)
            {
                if (string.IsNullOrWhiteSpace(_ibkrSettings.PaperAccountId))
                {
                    error = "PaperTrading requires IbkrSettings:PaperAccountId so the broker account can be verified.";
                    return BrokerEnvironmentVerificationStatus.Unknown;
                }

                if (string.Equals(connectedAccountId, _ibkrSettings.PaperAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    return BrokerEnvironmentVerificationStatus.VerifiedPaper;
                }

                error = $"Connected account does not match configured paper account. Connected={MaskAccount(connectedAccountId)}, ExpectedPaper={MaskAccount(_ibkrSettings.PaperAccountId)}.";
                return BrokerEnvironmentVerificationStatus.Mismatch;
            }

            if (!_tradingSettings.LiveTradingExplicitlyEnabled)
            {
                error = "LiveTrading requires TradingSettings:LiveTradingExplicitlyEnabled=true.";
                return BrokerEnvironmentVerificationStatus.Unknown;
            }

            if (string.IsNullOrWhiteSpace(_ibkrSettings.AccountId))
            {
                error = "LiveTrading requires IbkrSettings:AccountId so the broker account can be verified.";
                return BrokerEnvironmentVerificationStatus.Unknown;
            }

            if (string.Equals(connectedAccountId, _ibkrSettings.AccountId, StringComparison.OrdinalIgnoreCase))
            {
                return BrokerEnvironmentVerificationStatus.VerifiedLive;
            }

            error = $"Connected account does not match configured live account. Connected={MaskAccount(connectedAccountId)}, ExpectedLive={MaskAccount(_ibkrSettings.AccountId)}.";
            return BrokerEnvironmentVerificationStatus.Mismatch;
        }

        private static string MaskAccount(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return "(empty)";
            return accountId.Length <= 4 ? "****" : $"{accountId[..2]}***{accountId[^2..]}";
        }

        private static void ComparePositions(IReadOnlyList<PositionDto> brokerPositions, IReadOnlyList<TradingBot.Persistence.Trade> localOpenTrades, List<string> mismatches)
        {
            var brokerBySymbol = brokerPositions
                .Where(p => p.Quantity != 0m)
                .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity), StringComparer.OrdinalIgnoreCase);

            var localBySymbol = localOpenTrades
                .GroupBy(t => t.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(t => t.Size), StringComparer.OrdinalIgnoreCase);

            foreach (var symbol in brokerBySymbol.Keys.Union(localBySymbol.Keys, StringComparer.OrdinalIgnoreCase))
            {
                brokerBySymbol.TryGetValue(symbol, out var brokerQty);
                localBySymbol.TryGetValue(symbol, out var localQty);
                if (brokerQty != localQty)
                {
                    mismatches.Add($"Position mismatch for {symbol}: SQLite={localQty}, IBKR={brokerQty}.");
                }
            }
        }

        private static void CompareOpenOrders(IReadOnlyList<OrderStatusDto> brokerOrders, IReadOnlyList<TradingBot.Persistence.OrderRecord> localOpenOrders, List<string> mismatches)
        {
            var brokerIds = brokerOrders
                .Select(o => o.BrokerOrderId ?? o.OrderId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var localOrder in localOpenOrders.Where(o => !string.IsNullOrWhiteSpace(o.BrokerOrderId)))
            {
                if (!brokerIds.Contains(localOrder.BrokerOrderId))
                {
                    mismatches.Add($"Open order mismatch: SQLite contains broker order {localOrder.BrokerOrderId}, but IBKR does not.");
                }
            }

            var localIds = localOpenOrders
                .Select(o => o.BrokerOrderId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var brokerId in brokerIds)
            {
                if (!localIds.Contains(brokerId))
                {
                    mismatches.Add($"Open order mismatch: IBKR contains broker order {brokerId}, but SQLite does not.");
                }
            }
        }
    }
}
