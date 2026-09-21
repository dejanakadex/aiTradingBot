using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ExitManagementService : IExitManagementService, IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private const int AtrCandles = 20;

        private readonly IOrderExecutionService _executionService;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IOperatingModeService _operatingModeService;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IFeatureEngine _featureEngine;
        private readonly ExitStrategySettings _settings;
        private readonly TradingSettings _tradingSettings;
        private readonly ILogger<ExitManagementService> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _processedMarketUpdates = new(StringComparer.OrdinalIgnoreCase);

        public ExitManagementService(
            IOrderExecutionService executionService,
            IServiceProvider serviceProvider,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IOperatingModeService operatingModeService,
            ITradingEngineStatusService statusService,
            IFeatureEngine featureEngine,
            IOptions<ExitStrategySettings> settings,
            IOptions<TradingSettings> tradingSettings,
            ILogger<ExitManagementService> logger)
        {
            _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _operatingModeService = operatingModeService ?? throw new ArgumentNullException(nameof(operatingModeService));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
            _settings = settings?.Value ?? new ExitStrategySettings();
            _tradingSettings = tradingSettings?.Value ?? new TradingSettings();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _executionService.OrderStatusUpdated += OnOrderStatusUpdatedAsync;
            _executionService.OrderFilled += OnOrderFilledAsync;
        }

        public async Task RegisterApprovedEntryAsync(
            ApprovedTradePlan plan,
            ManagedOrderResult entryOrder,
            CancellationToken cancellationToken = default)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (entryOrder == null) throw new ArgumentNullException(nameof(entryOrder));
            if (string.IsNullOrWhiteSpace(entryOrder.BrokerOrderId)) return;
            if (plan.StrategyDecision.EntryMin == null || plan.StrategyDecision.StopPrice == null) return;

            if (_operatingModeService.CurrentMode == TradingOperatingMode.AnalysisOnly)
            {
                _logger.LogInformation("Exit management registration skipped in AnalysisOnly mode for {Symbol}.", plan.StrategyDecision.Symbol);
                return;
            }

            var initialEntry = entryOrder.AverageFillPrice ?? plan.StrategyDecision.EntryMin.Value;
            var initialStop = plan.StrategyDecision.StopPrice.Value;
            var initialRisk = initialEntry - initialStop;
            if (initialRisk <= 0m)
            {
                _logger.LogError("Cannot register exit management for {Symbol}; invalid initial risk per share {Risk}.", plan.StrategyDecision.Symbol, initialRisk);
                _statusService.SetState(TradingEngineState.Degraded, false, "Exit management could not register invalid stop/risk.", new[] { "Initial risk per share must be positive." });
                return;
            }

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var exists = await db.ExitManagementRecords
                .AnyAsync(x => x.EntryBrokerOrderId == entryOrder.BrokerOrderId, cancellationToken)
                .ConfigureAwait(false);
            if (!exists)
            {
                db.ExitManagementRecords.Add(new TradingBot.Persistence.ExitManagementRecord
                {
                    Symbol = plan.StrategyDecision.Symbol,
                    EntryBrokerOrderId = entryOrder.BrokerOrderId,
                    State = ExitManagementState.WaitingForEntryFill,
                    InitialEntryPrice = initialEntry,
                    InitialStopPrice = initialStop,
                    InitialRiskPerShare = initialRisk,
                    FilledQuantity = 0m,
                    ProtectedQuantity = 0m,
                    HighestPriceSinceEntry = initialEntry,
                    CurrentProtectiveStop = initialStop,
                    TrailingAtrTimeframe = NormalizeTimeframe(_settings.TrailingAtrTimeframe),
                    TrailingAtrMultiplier = _settings.TrailingAtrMultiplier,
                    OpenedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                    RawJson = JsonSerializer.Serialize(new
                    {
                        plan.Pattern,
                        plan.AiAnalysis,
                        plan.AiCriticAnalysis,
                        plan.StrategyDecision,
                        plan.RiskDecision,
                        exitStrategy = _settings
                    }, JsonOptions)
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            if (entryOrder.FilledQuantity > 0m)
            {
                await HandleOrderUpdateAsync(new OrderStatusDto
                {
                    OrderId = entryOrder.OrderId,
                    BrokerOrderId = entryOrder.BrokerOrderId,
                    Status = entryOrder.Status.ToString(),
                    FilledQuantity = entryOrder.FilledQuantity,
                    RemainingQuantity = entryOrder.RemainingQuantity,
                    AverageFillPrice = entryOrder.AverageFillPrice,
                    TimestampUtc = DateTime.UtcNow,
                    Message = "Entry already reported filled during submission."
                }, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task RestoreAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var unsafeRecords = await db.ExitManagementRecords
                .Where(x => x.State != ExitManagementState.Closed
                    && x.FilledQuantity > 0m
                    && (x.ProtectiveStopBrokerOrderId == string.Empty || x.ProtectedQuantity < x.FilledQuantity))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (unsafeRecords.Count == 0) return;

            var mismatches = unsafeRecords
                .Select(x => $"Exit state for {x.Symbol} entry {x.EntryBrokerOrderId} has filled quantity {x.FilledQuantity} but protected quantity {x.ProtectedQuantity}.")
                .ToArray();

            _statusService.SetState(TradingEngineState.Degraded, false, "Exit-management state requires reconciliation. Trading disabled.", mismatches);
            foreach (var mismatch in mismatches)
            {
                _logger.LogError("Exit-management reconciliation mismatch: {Mismatch}", mismatch);
            }
        }

        public async Task ProcessMarketCandleAsync(Candle candle, CancellationToken cancellationToken = default)
        {
            if (candle == null) throw new ArgumentNullException(nameof(candle));
            if (_settings.Mode != ExitStrategyMode.AtrTrailing) return;

            var timeframe = NormalizeTimeframe(_settings.TrailingAtrTimeframe);
            if (!string.Equals(ToConfigTimeframe(candle.Timeframe), timeframe, StringComparison.OrdinalIgnoreCase)) return;

            var updateKey = $"{candle.Symbol}:{candle.Timeframe}:{candle.TimestampUtc:O}:{candle.Close}";
            if (!_processedMarketUpdates.TryAdd(updateKey, 0)) return;

            var gate = await EnterLockAsync(candle.Symbol, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var records = await db.ExitManagementRecords
                    .Where(x => x.Symbol == candle.Symbol
                        && x.State != ExitManagementState.Closed
                        && x.State != ExitManagementState.Faulted
                        && x.FilledQuantity > 0m
                        && x.ProtectedQuantity > 0m)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var record in records)
                {
                    await ProcessMarketCandleForRecordAsync(db, record, candle, cancellationToken).ConfigureAwait(false);
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        private Task OnOrderStatusUpdatedAsync(OrderStatusDto status)
        {
            return HandleOrderUpdateAsync(status, CancellationToken.None);
        }

        private Task OnOrderFilledAsync(OrderStatusDto status)
        {
            return HandleOrderUpdateAsync(status, CancellationToken.None);
        }

        private async Task HandleOrderUpdateAsync(OrderStatusDto status, CancellationToken cancellationToken)
        {
            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            if (string.IsNullOrWhiteSpace(brokerOrderId)) return;

            var gate = await EnterLockAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var entryRecord = await db.ExitManagementRecords
                    .Where(x => x.EntryBrokerOrderId == brokerOrderId)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (entryRecord != null)
                {
                    await HandleEntryFillAsync(db, entryRecord, status, cancellationToken).ConfigureAwait(false);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var stopRecord = await db.ExitManagementRecords
                    .Where(x => x.ProtectiveStopBrokerOrderId == brokerOrderId)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (stopRecord != null && IsTerminalFilled(status))
                {
                    await CloseRecordAsync(db, stopRecord, status, cancellationToken).ConfigureAwait(false);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or JsonException)
            {
                _logger.LogError(ex, "Exit-management order update failed for brokerOrderId={BrokerOrderId}. Trading must fail closed.", brokerOrderId);
                _statusService.SetState(TradingEngineState.Degraded, false, "Exit management failed while processing broker update.", new[] { ex.Message });
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task HandleEntryFillAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            OrderStatusDto status,
            CancellationToken cancellationToken)
        {
            if (status.FilledQuantity <= 0m) return;
            if (status.FilledQuantity <= record.FilledQuantity && record.ProtectedQuantity >= status.FilledQuantity) return;

            var averageEntry = status.AverageFillPrice ?? record.InitialEntryPrice;
            if (averageEntry > 0m && record.FilledQuantity == 0m)
            {
                record.InitialEntryPrice = averageEntry;
                record.InitialRiskPerShare = averageEntry - record.InitialStopPrice;
                record.HighestPriceSinceEntry = Math.Max(record.HighestPriceSinceEntry, averageEntry);
            }

            if (record.InitialRiskPerShare <= 0m)
            {
                await MarkFaultedAsync(db, record, "Initial risk per share is invalid after fill.", cancellationToken).ConfigureAwait(false);
                return;
            }

            record.FilledQuantity = status.FilledQuantity;
            record.UpdatedUtc = DateTime.UtcNow;
            if (record.TradeId == null)
            {
                var trade = new TradingBot.Persistence.Trade
                {
                    Symbol = record.Symbol,
                    OpenedUtc = status.TimestampUtc == default ? DateTime.UtcNow : status.TimestampUtc,
                    Size = status.FilledQuantity,
                    EntryPrice = averageEntry,
                    Notes = JsonSerializer.Serialize(new
                    {
                        source = "ExitManagement",
                        entryBrokerOrderId = record.EntryBrokerOrderId,
                        initialStopPrice = record.InitialStopPrice,
                        initialRiskPerShare = record.InitialRiskPerShare,
                        exitStrategy = _settings.Mode.ToString()
                    }, JsonOptions)
                };
                db.Trades.Add(trade);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                record.TradeId = trade.Id;
            }
            else
            {
                var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == record.TradeId.Value, cancellationToken).ConfigureAwait(false);
                if (trade != null)
                {
                    trade.Size = status.FilledQuantity;
                    trade.EntryPrice = averageEntry;
                }
            }

            if (record.ProtectedQuantity == 0m)
            {
                await SubmitInitialProtectiveStopAsync(db, record, averageEntry, cancellationToken).ConfigureAwait(false);
            }
            else if (record.ProtectedQuantity < status.FilledQuantity)
            {
                await ModifyProtectiveStopAsync(
                    db,
                    record,
                    record.CurrentProtectiveStop,
                    ExitStopUpdateReason.PartialFillProtectionAdjusted,
                    averageEntry,
                    currentAtr: null,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SubmitInitialProtectiveStopAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            decimal marketPrice,
            CancellationToken cancellationToken)
        {
            if (!CanSubmitProtectiveBrokerOrder(out var reason))
            {
                await MarkFaultedAsync(db, record, reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            var request = BuildProtectiveStopRequest(record, record.FilledQuantity, record.CurrentProtectiveStop, "ProtectiveStop");
            OrderStatusDto status;
            try
            {
                status = await _executionService.SubmitOrderAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                _logger.LogError(ex, "Failed to submit initial protective stop for {Symbol} entry {EntryBrokerOrderId}.", record.Symbol, record.EntryBrokerOrderId);
                await MarkFaultedAsync(db, record, $"Initial protective stop submission failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return;
            }

            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            await PersistOrderRecordAsync(db, brokerOrderId, request, "ProtectiveStop", status, cancellationToken).ConfigureAwait(false);
            if (IsRejected(status))
            {
                await MarkFaultedAsync(db, record, status.Message ?? "Broker rejected initial protective stop.", cancellationToken).ConfigureAwait(false);
                await AddAuditAsync(db, record, null, record.CurrentProtectiveStop, ExitStopUpdateReason.RejectedStop, marketPrice, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            record.ProtectiveStopBrokerOrderId = brokerOrderId;
            record.ProtectedQuantity = record.FilledQuantity;
            record.State = ExitManagementState.InitialProtection;
            record.UpdatedUtc = DateTime.UtcNow;
            await AddAuditAsync(db, record, null, record.CurrentProtectiveStop, ExitStopUpdateReason.InitialProtection, marketPrice, null, cancellationToken).ConfigureAwait(false);
        }

        private async Task ProcessMarketCandleForRecordAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            Candle candle,
            CancellationToken cancellationToken)
        {
            var marketPrice = candle.Close;
            record.HighestPriceSinceEntry = Math.Max(record.HighestPriceSinceEntry, candle.High);

            if (_settings.MaximumHoldingMinutes.HasValue
                && _settings.MaximumHoldingMinutes.Value > 0
                && DateTime.UtcNow - record.OpenedUtc >= TimeSpan.FromMinutes(_settings.MaximumHoldingMinutes.Value))
            {
                await SubmitMaximumHoldingExitAsync(db, record, marketPrice, cancellationToken).ConfigureAwait(false);
                return;
            }

            var rMultiple = CalculateRMultiple(record, marketPrice);
            if (!record.BreakEvenActivated && rMultiple >= _settings.BreakEvenTriggerR)
            {
                var breakEvenStop = record.InitialEntryPrice + (record.InitialRiskPerShare * _settings.BreakEvenOffsetR);
                await TryRaiseStopAsync(
                    db,
                    record,
                    breakEvenStop,
                    ExitStopUpdateReason.BreakEvenActivated,
                    marketPrice,
                    currentAtr: null,
                    cancellationToken).ConfigureAwait(false);
                record.BreakEvenActivated = true;
                if (record.State < ExitManagementState.BreakEvenProtection) record.State = ExitManagementState.BreakEvenProtection;
            }

            if (rMultiple < _settings.TrailingActivationR) return;

            var currentAtr = await CalculateAtrAsync(db, record.Symbol, candle, cancellationToken).ConfigureAwait(false);
            if (currentAtr == null || currentAtr <= 0m) return;

            var trailingStop = record.HighestPriceSinceEntry - (currentAtr.Value * _settings.TrailingAtrMultiplier);
            var reason = record.TrailingActivated
                ? ExitStopUpdateReason.TrailingAdvanced
                : ExitStopUpdateReason.TrailingActivated;
            await TryRaiseStopAsync(db, record, trailingStop, reason, marketPrice, currentAtr, cancellationToken).ConfigureAwait(false);
            record.TrailingActivated = true;
            record.State = ExitManagementState.Trailing;
        }

        private async Task TryRaiseStopAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            decimal proposedStop,
            ExitStopUpdateReason reason,
            decimal marketPrice,
            decimal? currentAtr,
            CancellationToken cancellationToken)
        {
            if (proposedStop <= record.CurrentProtectiveStop) return;
            if (proposedStop >= marketPrice) return;

            await ModifyProtectiveStopAsync(db, record, proposedStop, reason, marketPrice, currentAtr, cancellationToken).ConfigureAwait(false);
        }

        private async Task ModifyProtectiveStopAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            decimal newStop,
            ExitStopUpdateReason reason,
            decimal marketPrice,
            decimal? currentAtr,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(record.ProtectiveStopBrokerOrderId))
            {
                await SubmitInitialProtectiveStopAsync(db, record, marketPrice, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!CanSubmitProtectiveBrokerOrder(out var safetyReason))
            {
                await MarkFaultedAsync(db, record, safetyReason, cancellationToken).ConfigureAwait(false);
                return;
            }

            var modificationService = _serviceProvider.GetService<IOrderModificationService>();
            if (modificationService == null)
            {
                await MarkFaultedAsync(db, record, "Broker order modification service is unavailable; refusing cancel/recreate stop because it can create an unprotected window.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var oldStop = record.CurrentProtectiveStop;
            var request = BuildProtectiveStopRequest(record, record.FilledQuantity, newStop, "ProtectiveStopModify");
            OrderStatusDto status;
            try
            {
                status = await modificationService.ModifyOrderAsync(record.ProtectiveStopBrokerOrderId, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                _logger.LogError(ex, "Failed to modify protective stop {BrokerOrderId} for {Symbol}.", record.ProtectiveStopBrokerOrderId, record.Symbol);
                await MarkFaultedAsync(db, record, $"Protective stop modification failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
                return;
            }

            await PersistOrderRecordAsync(db, record.ProtectiveStopBrokerOrderId, request, "ProtectiveStopModify", status, cancellationToken).ConfigureAwait(false);
            if (IsRejected(status))
            {
                await MarkFaultedAsync(db, record, status.Message ?? "Broker rejected protective stop modification.", cancellationToken).ConfigureAwait(false);
                await AddAuditAsync(db, record, oldStop, newStop, ExitStopUpdateReason.RejectedStop, marketPrice, currentAtr, cancellationToken).ConfigureAwait(false);
                return;
            }

            record.CurrentProtectiveStop = newStop;
            record.ProtectedQuantity = record.FilledQuantity;
            record.UpdatedUtc = DateTime.UtcNow;
            await AddAuditAsync(db, record, oldStop, newStop, reason, marketPrice, currentAtr, cancellationToken).ConfigureAwait(false);
        }

        private async Task SubmitMaximumHoldingExitAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            decimal marketPrice,
            CancellationToken cancellationToken)
        {
            if (!CanSubmitProtectiveBrokerOrder(out var reason))
            {
                await MarkFaultedAsync(db, record, reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            var request = new OrderRequestDto
            {
                Symbol = record.Symbol,
                Quantity = record.FilledQuantity,
                Side = "SELL",
                Type = "MARKET",
                CreatedAtUtc = DateTime.UtcNow,
                Role = "MaximumHoldingTimeExit",
                Transmit = true
            };

            try
            {
                var status = await _executionService.SubmitOrderAsync(request, cancellationToken).ConfigureAwait(false);
                await PersistOrderRecordAsync(db, status.BrokerOrderId ?? status.OrderId, request, "MaximumHoldingTimeExit", status, cancellationToken).ConfigureAwait(false);
                await AddAuditAsync(db, record, record.CurrentProtectiveStop, record.CurrentProtectiveStop, ExitStopUpdateReason.MaximumHoldingTimeExit, marketPrice, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                await MarkFaultedAsync(db, record, $"Maximum holding exit submission failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task CloseRecordAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            OrderStatusDto status,
            CancellationToken cancellationToken)
        {
            record.State = ExitManagementState.Closed;
            record.ClosedUtc = status.TimestampUtc == default ? DateTime.UtcNow : status.TimestampUtc;
            record.UpdatedUtc = DateTime.UtcNow;

            if (record.TradeId.HasValue)
            {
                var trade = await db.Trades.FirstOrDefaultAsync(t => t.Id == record.TradeId.Value, cancellationToken).ConfigureAwait(false);
                if (trade != null)
                {
                    trade.ClosedUtc = record.ClosedUtc;
                    trade.ExitPrice = status.AverageFillPrice ?? status.LastFillPrice;
                }
            }
        }

        private bool CanSubmitProtectiveBrokerOrder(out string reason)
        {
            var mode = _operatingModeService.CurrentMode;
            if (mode == TradingOperatingMode.AnalysisOnly)
            {
                reason = "AnalysisOnly mode never submits broker exit/protective orders.";
                return false;
            }

            if (mode == TradingOperatingMode.LiveTrading && !_tradingSettings.LiveTradingExplicitlyEnabled)
            {
                reason = "LiveTradingExplicitlyEnabled is false; broker exit/protective order rejected.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private static OrderRequestDto BuildProtectiveStopRequest(
            TradingBot.Persistence.ExitManagementRecord record,
            decimal quantity,
            decimal stopPrice,
            string role)
        {
            return new OrderRequestDto
            {
                Symbol = record.Symbol,
                Quantity = quantity,
                Side = "SELL",
                Type = "STOP",
                StopPrice = stopPrice,
                CreatedAtUtc = DateTime.UtcNow,
                Role = role,
                Transmit = true
            };
        }

        private async Task PersistOrderRecordAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            string brokerOrderId,
            OrderRequestDto request,
            string role,
            OrderStatusDto status,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId)) return;

            var existing = await db.OrderRecords
                .Where(o => o.BrokerOrderId == brokerOrderId)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var rawJson = JsonSerializer.Serialize(new
            {
                role,
                request,
                brokerStatus = status,
                stopPrice = request.StopPrice,
                quantity = request.Quantity
            }, JsonOptions);

            if (existing == null)
            {
                db.OrderRecords.Add(new TradingBot.Persistence.OrderRecord
                {
                    BrokerOrderId = brokerOrderId,
                    ClientOrderKey = $"{role}:{brokerOrderId}:{request.Symbol}",
                    Symbol = request.Symbol,
                    CreatedUtc = DateTime.UtcNow,
                    Status = ParseOrderStatus(status.Status),
                    RawJson = rawJson
                });
            }
            else
            {
                existing.Symbol = string.IsNullOrWhiteSpace(existing.Symbol) ? request.Symbol : existing.Symbol;
                existing.Status = ParseOrderStatus(status.Status);
                existing.RawJson = rawJson;
            }
        }

        private async Task AddAuditAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            decimal? oldStop,
            decimal newStop,
            ExitStopUpdateReason reason,
            decimal marketPrice,
            decimal? currentAtr,
            CancellationToken cancellationToken)
        {
            db.ExitStopAuditRecords.Add(new TradingBot.Persistence.ExitStopAuditRecord
            {
                TradeId = record.TradeId,
                Symbol = record.Symbol,
                EntryBrokerOrderId = record.EntryBrokerOrderId,
                BrokerOrderId = record.ProtectiveStopBrokerOrderId,
                OldStop = oldStop,
                NewStop = newStop,
                Reason = reason.ToString(),
                MarketPrice = marketPrice,
                HighestPriceSinceEntry = record.HighestPriceSinceEntry,
                CurrentAtr = currentAtr,
                CurrentRMultiple = CalculateRMultiple(record, marketPrice),
                ExitManagementState = record.State.ToString(),
                TimestampUtc = DateTime.UtcNow
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Exit stop update {Reason} for {Symbol}: oldStop={OldStop}, newStop={NewStop}, marketPrice={MarketPrice}, r={RMultiple}",
                reason,
                record.Symbol,
                oldStop,
                newStop,
                marketPrice,
                CalculateRMultiple(record, marketPrice));
        }

        private async Task MarkFaultedAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            TradingBot.Persistence.ExitManagementRecord record,
            string reason,
            CancellationToken cancellationToken)
        {
            record.State = ExitManagementState.Faulted;
            record.UpdatedUtc = DateTime.UtcNow;
            record.RawJson = JsonSerializer.Serialize(new
            {
                previous = record.RawJson,
                faultReason = reason,
                faultedAtUtc = DateTime.UtcNow
            }, JsonOptions);

            _statusService.SetState(TradingEngineState.Degraded, false, "Exit management fault. Trading disabled.", new[] { reason });
            _logger.LogError("Exit management fault for {Symbol} entry {EntryBrokerOrderId}: {Reason}", record.Symbol, record.EntryBrokerOrderId, reason);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<decimal?> CalculateAtrAsync(
            TradingBot.Persistence.TradingBotDbContext db,
            string symbol,
            Candle latest,
            CancellationToken cancellationToken)
        {
            var timeframe = latest.Timeframe;
            var candles = await db.Candles.AsNoTracking()
                .Where(c => c.Symbol == symbol && c.Timeframe == timeframe)
                .OrderByDescending(c => c.TimestampUtc)
                .Take(AtrCandles)
                .Select(c => new Candle(c.Symbol, c.Timeframe, c.TimestampUtc, c.Open, c.High, c.Low, c.Close, c.Volume))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!candles.Any(c => c.TimestampUtc == latest.TimestampUtc))
            {
                candles.Add(latest);
            }

            candles = candles.OrderBy(c => c.TimestampUtc).ToList();
            if (candles.Count < 2) return null;

            var features = _featureEngine.ComputeFeatures(candles);
            return features.Atr;
        }

        private static decimal CalculateRMultiple(TradingBot.Persistence.ExitManagementRecord record, decimal marketPrice)
        {
            return record.InitialRiskPerShare <= 0m
                ? 0m
                : (marketPrice - record.InitialEntryPrice) / record.InitialRiskPerShare;
        }

        private async Task<SemaphoreSlim> EnterLockAsync(string key, CancellationToken cancellationToken)
        {
            var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return gate;
        }

        private static bool IsTerminalFilled(OrderStatusDto status)
        {
            return status.FilledQuantity > 0m && status.RemainingQuantity == 0m
                && status.Status.Equals("Filled", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRejected(OrderStatusDto status)
        {
            return status.Status.Equals("Rejected", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeTimeframe(string timeframe)
        {
            return timeframe.Trim().ToLowerInvariant() switch
            {
                "1m" or "1 min" or "1 minute" => "1m",
                "5m" or "5 mins" or "5 minutes" => "5m",
                "15m" or "15 mins" or "15 minutes" => "15m",
                _ => "1m"
            };
        }

        private static string ToConfigTimeframe(Timeframe timeframe)
        {
            return timeframe switch
            {
                Timeframe.OneMinute => "1m",
                Timeframe.FiveMinutes => "5m",
                Timeframe.FifteenMinutes => "15m",
                _ => timeframe.ToString()
            };
        }

        private static OrderStatus ParseOrderStatus(string status)
        {
            return status switch
            {
                var s when s.Equals("New", StringComparison.OrdinalIgnoreCase) => OrderStatus.New,
                var s when s.Equals("PendingBrokerConfirmation", StringComparison.OrdinalIgnoreCase) => OrderStatus.PendingBrokerConfirmation,
                var s when s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) => OrderStatus.Unknown,
                var s when s.Equals("Submitted", StringComparison.OrdinalIgnoreCase) => OrderStatus.Submitted,
                var s when s.Equals("PreSubmitted", StringComparison.OrdinalIgnoreCase) => OrderStatus.Submitted,
                var s when s.Equals("PendingSubmit", StringComparison.OrdinalIgnoreCase) => OrderStatus.Submitted,
                var s when s.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase) => OrderStatus.PartiallyFilled,
                var s when s.Equals("Partial", StringComparison.OrdinalIgnoreCase) => OrderStatus.PartiallyFilled,
                var s when s.Equals("Filled", StringComparison.OrdinalIgnoreCase) => OrderStatus.Filled,
                var s when s.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) => OrderStatus.Cancelled,
                var s when s.Equals("Canceled", StringComparison.OrdinalIgnoreCase) => OrderStatus.Cancelled,
                var s when s.Equals("Rejected", StringComparison.OrdinalIgnoreCase) => OrderStatus.Rejected,
                _ => OrderStatus.Submitted
            };
        }

        public void Dispose()
        {
            _executionService.OrderStatusUpdated -= OnOrderStatusUpdatedAsync;
            _executionService.OrderFilled -= OnOrderFilledAsync;
        }
    }
}
