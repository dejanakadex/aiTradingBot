using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Exceptions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class OrderManager : IOrderManager, IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly IOrderExecutionService _executionService;
        private readonly ITradingExecutionGuard _executionGuard;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly ILogger<OrderManager> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _submissionLocks = new();

        public OrderManager(
            IOrderExecutionService executionService,
            ITradingExecutionGuard executionGuard,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<OrderManager> logger)
        {
            _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
            _executionGuard = executionGuard ?? throw new ArgumentNullException(nameof(executionGuard));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _executionService.OrderStatusUpdated += OnOrderStatusUpdatedAsync;
            _executionService.OrderFilled += OnOrderFilledAsync;
        }

        public async Task<ManagedOrderResult> SubmitLimitBuyAsync(
            OrderRequest orderRequest,
            RiskDecision riskDecision,
            CancellationToken cancellationToken = default)
        {
            if (orderRequest == null) throw new ArgumentNullException(nameof(orderRequest));
            if (orderRequest.Side != OrderSide.Buy || orderRequest.Type != OrderType.Limit)
            {
                throw new ArgumentException("Only approved limit buy orders can be submitted with this method.", nameof(orderRequest));
            }

            var guard = await _executionGuard.CanSubmitBrokerOrderAsync(riskDecision, cancellationToken).ConfigureAwait(false);
            if (!guard.Approved)
            {
                _logger.LogWarning("Limit buy rejected by execution guard for {Symbol}: {Reason}", orderRequest.Symbol, guard.Reason);
                return RejectedByGuard(guard);
            }

            return await SubmitSingleOrderAsync(orderRequest, "LimitBuy", cancellationToken).ConfigureAwait(false);
        }

        public async Task<ManagedOrderResult> SubmitBracketOrderAsync(
            OrderRequest entryLimitBuy,
            OrderRequest stopLoss,
            OrderRequest takeProfit,
            RiskDecision riskDecision,
            CancellationToken cancellationToken = default)
        {
            if (entryLimitBuy == null) throw new ArgumentNullException(nameof(entryLimitBuy));
            if (stopLoss == null) throw new ArgumentNullException(nameof(stopLoss));
            if (takeProfit == null) throw new ArgumentNullException(nameof(takeProfit));
            if (entryLimitBuy.Side != OrderSide.Buy || entryLimitBuy.Type != OrderType.Limit)
            {
                throw new ArgumentException("Bracket entry must be a limit buy.", nameof(entryLimitBuy));
            }
            if (stopLoss.Side != OrderSide.Sell || stopLoss.Type != OrderType.Stop)
            {
                throw new ArgumentException("Bracket stop loss must be a sell stop order.", nameof(stopLoss));
            }
            if (takeProfit.Side != OrderSide.Sell || takeProfit.Type != OrderType.Limit)
            {
                throw new ArgumentException("Bracket take profit must be a sell limit order.", nameof(takeProfit));
            }

            var guard = await _executionGuard.CanSubmitBrokerOrderAsync(riskDecision, cancellationToken).ConfigureAwait(false);
            if (!guard.Approved)
            {
                _logger.LogWarning("Bracket order rejected by execution guard for {Symbol}: {Reason}", entryLimitBuy.Symbol, guard.Reason);
                return RejectedByGuard(guard);
            }

            var idempotencyKey = BuildBracketIdempotencyKey(entryLimitBuy, stopLoss, takeProfit);
            var submissionLock = _submissionLocks.GetOrAdd(idempotencyKey, _ => new SemaphoreSlim(1, 1));
            await submissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var duplicate = await FindDuplicateAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
                if (duplicate != null)
                {
                    return CopyDuplicateResult(duplicate, "Duplicate bracket order submission prevented.");
                }

                var entry = await SubmitOrderToBrokerAsync(entryLimitBuy, "BracketEntry", idempotencyKey, null, cancellationToken).ConfigureAwait(false);
                if (!entry.Submitted || string.IsNullOrWhiteSpace(entry.BrokerOrderId))
                {
                    return entry;
                }

                var childOrderIds = new List<string>();

                var stop = await SubmitOrderToBrokerAsync(stopLoss, "ProtectiveStop", idempotencyKey, entry.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(stop.BrokerOrderId)) childOrderIds.Add(stop.BrokerOrderId);

                var target = await SubmitOrderToBrokerAsync(takeProfit, "TakeProfit", idempotencyKey, entry.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(target.BrokerOrderId)) childOrderIds.Add(target.BrokerOrderId);

                return new ManagedOrderResult
                {
                    OrderId = entry.OrderId,
                    BrokerOrderId = entry.BrokerOrderId,
                    Status = entry.Status,
                    Submitted = entry.Submitted,
                    IsDuplicate = false,
                    FilledQuantity = entry.FilledQuantity,
                    RemainingQuantity = entry.RemainingQuantity,
                    AverageFillPrice = entry.AverageFillPrice,
                    Commission = entry.Commission,
                    ChildOrderIds = childOrderIds,
                    Message = childOrderIds.Count == 2
                        ? "Bracket workflow submitted. Fill must be confirmed by broker status updates."
                        : "Bracket entry submitted, but one or more protective child orders were not accepted by the execution adapter."
                };
            }
            finally
            {
                submissionLock.Release();
            }
        }

        public async Task<bool> CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId)) throw new ArgumentException("brokerOrderId required", nameof(brokerOrderId));

            var cancelled = await _executionService.CancelOrderAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
            if (cancelled)
            {
                await UpsertOrderStatusAsync(new OrderStatusDto
                {
                    OrderId = brokerOrderId,
                    BrokerOrderId = brokerOrderId,
                    Status = "Cancelled",
                    TimestampUtc = DateTime.UtcNow,
                    Message = "Cancel acknowledged by execution adapter."
                }, cancellationToken).ConfigureAwait(false);
            }

            return cancelled;
        }

        public async Task<ManagedOrderResult?> GetOrderStatusAsync(string brokerOrderId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(brokerOrderId)) throw new ArgumentException("brokerOrderId required", nameof(brokerOrderId));

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.OrderRecords.AsNoTracking()
                .Where(o => o.BrokerOrderId == brokerOrderId)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return record == null ? null : ToManagedOrderResult(record, submitted: false, duplicate: false);
        }

        private async Task<ManagedOrderResult> SubmitSingleOrderAsync(OrderRequest orderRequest, string role, CancellationToken cancellationToken)
        {
            var idempotencyKey = BuildOrderIdempotencyKey(orderRequest, role, parentBrokerOrderId: null);
            var submissionLock = _submissionLocks.GetOrAdd(idempotencyKey, _ => new SemaphoreSlim(1, 1));
            await submissionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var duplicate = await FindDuplicateAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
                if (duplicate != null)
                {
                    return CopyDuplicateResult(duplicate, "Duplicate order submission prevented.");
                }

                return await SubmitOrderToBrokerAsync(orderRequest, role, idempotencyKey, null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                submissionLock.Release();
            }
        }

        private async Task<ManagedOrderResult> SubmitOrderToBrokerAsync(
            OrderRequest orderRequest,
            string role,
            string idempotencyKey,
            string? parentBrokerOrderId,
            CancellationToken cancellationToken)
        {
            OrderStatusDto status;
            try
            {
                status = await _executionService.SubmitOrderAsync(ToDto(orderRequest, role, parentBrokerOrderId), cancellationToken).ConfigureAwait(false);
            }
            catch (BrokerOrderStateUnknownException ex)
            {
                _logger.LogError(ex, "Order state unknown after broker submission attempt for {Symbol} {Role}. New submissions with the same key will be blocked until reconciliation.", orderRequest.Symbol, role);
                var rawUnknown = JsonSerializer.Serialize(new
                {
                    idempotencyKey,
                    role,
                    parentBrokerOrderId,
                    request = orderRequest,
                    message = ex.Message,
                    state = "PendingBrokerConfirmation"
                }, JsonOptions);

                await PersistOrderAsync(string.Empty, idempotencyKey, orderRequest.Symbol, OrderStatus.PendingBrokerConfirmation, rawUnknown, cancellationToken).ConfigureAwait(false);

                return new ManagedOrderResult
                {
                    Status = OrderStatus.PendingBrokerConfirmation,
                    Submitted = false,
                    Message = "Broker may have accepted the order, but confirmation was lost. Reconciliation is required before retry."
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or TaskCanceledException)
            {
                _logger.LogWarning(ex, "Order submission failed for {Symbol} {Role}", orderRequest.Symbol, role);
                status = new OrderStatusDto
                {
                    OrderId = string.Empty,
                    Status = "Rejected",
                    TimestampUtc = DateTime.UtcNow,
                    Message = $"Execution adapter unavailable during {role} submission."
                };
            }

            var orderStatus = ParseOrderStatus(status.Status);
            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            var raw = BuildOrderRawJson(orderRequest, status, role, idempotencyKey, parentBrokerOrderId);

            await PersistOrderAsync(brokerOrderId, $"{idempotencyKey}:{role}", orderRequest.Symbol, orderStatus, raw, cancellationToken).ConfigureAwait(false);

            return new ManagedOrderResult
            {
                OrderId = status.OrderId,
                BrokerOrderId = brokerOrderId,
                Status = orderStatus,
                Submitted = orderStatus is OrderStatus.Submitted or OrderStatus.New or OrderStatus.PartiallyFilled or OrderStatus.Filled,
                FilledQuantity = status.FilledQuantity,
                RemainingQuantity = status.RemainingQuantity,
                AverageFillPrice = status.AverageFillPrice,
                Commission = status.Commission,
                Message = orderStatus == OrderStatus.Filled
                    ? "Broker reported order filled."
                    : orderStatus == OrderStatus.Rejected
                        ? status.Message ?? "Broker rejected order."
                        : "Order submitted to execution adapter. Fill must be confirmed by broker status updates."
            };
        }

        private async Task<ManagedOrderResult?> FindDuplicateAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.OrderRecords.AsNoTracking()
                .Where(o => o.ClientOrderKey.StartsWith(idempotencyKey)
                    && o.Status != OrderStatus.Cancelled
                    && o.Status != OrderStatus.Rejected)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return record == null ? null : ToManagedOrderResult(record, submitted: false, duplicate: true);
        }

        private async Task OnOrderStatusUpdatedAsync(OrderStatusDto status)
        {
            await UpsertOrderStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task OnOrderFilledAsync(OrderStatusDto status)
        {
            await UpsertOrderStatusAsync(status, CancellationToken.None).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(status.BrokerExecutionId))
            {
                await PersistExecutionAsync(status, CancellationToken.None).ConfigureAwait(false);
            }
        }

        private async Task UpsertOrderStatusAsync(OrderStatusDto status, CancellationToken cancellationToken)
        {
            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            if (string.IsNullOrWhiteSpace(brokerOrderId)) return;

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var order = await db.OrderRecords
                .Where(o => o.BrokerOrderId == brokerOrderId)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var raw = JsonSerializer.Serialize(new
            {
                brokerStatus = status,
                averageFillPrice = status.AverageFillPrice,
                filledQuantity = status.FilledQuantity,
                remainingQuantity = status.RemainingQuantity,
                commission = status.Commission,
                message = status.Message
            }, JsonOptions);

            if (order == null)
            {
                db.OrderRecords.Add(new TradingBot.Persistence.OrderRecord
                {
                    BrokerOrderId = brokerOrderId,
                    Symbol = string.Empty,
                    CreatedUtc = status.TimestampUtc == default ? DateTime.UtcNow : status.TimestampUtc,
                    Status = ParseOrderStatus(status.Status),
                    RawJson = raw
                });
            }
            else
            {
                order.Status = ParseOrderStatus(status.Status);
                order.RawJson = raw;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task PersistExecutionAsync(OrderStatusDto status, CancellationToken cancellationToken)
        {
            var brokerOrderId = status.BrokerOrderId ?? status.OrderId;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var order = await db.OrderRecords.AsNoTracking()
                .Where(o => o.BrokerOrderId == brokerOrderId)
                .OrderByDescending(o => o.CreatedUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (order == null) return;

            var executionId = status.BrokerExecutionId ?? $"{brokerOrderId}:{status.TimestampUtc:O}:{status.FilledQuantity}";
            var exists = await db.ExecutionRecords.AnyAsync(e => e.BrokerExecutionId == executionId, cancellationToken).ConfigureAwait(false);
            if (exists) return;

            db.ExecutionRecords.Add(new TradingBot.Persistence.ExecutionRecord
            {
                BrokerExecutionId = executionId,
                OrderRecordId = order.Id,
                Status = ParseOrderStatus(status.Status) == OrderStatus.Filled ? ExecutionStatus.Completed : ExecutionStatus.Executing,
                TimestampUtc = status.TimestampUtc == default ? DateTime.UtcNow : status.TimestampUtc,
                RawJson = JsonSerializer.Serialize(new
                {
                    brokerOrderId,
                    brokerExecutionId = executionId,
                    fillPrice = status.LastFillPrice,
                    averageFillPrice = status.AverageFillPrice,
                    filledQuantity = status.FilledQuantity,
                    remainingQuantity = status.RemainingQuantity,
                    commission = status.Commission,
                    message = status.Message
                }, JsonOptions)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task PersistOrderAsync(string? brokerOrderId, string clientOrderKey, string symbol, OrderStatus status, string rawJson, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.OrderRecords.Add(new TradingBot.Persistence.OrderRecord
            {
                BrokerOrderId = brokerOrderId ?? string.Empty,
                ClientOrderKey = clientOrderKey,
                Symbol = symbol,
                CreatedUtc = DateTime.UtcNow,
                Status = status,
                RawJson = rawJson
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private static OrderRequestDto ToDto(OrderRequest request, string role, string? parentBrokerOrderId)
        {
            return new OrderRequestDto
            {
                Symbol = request.Symbol,
                Quantity = request.Quantity,
                LimitPrice = request.Type is OrderType.Limit or OrderType.StopLimit ? request.Price : null,
                StopPrice = request.Type is OrderType.Stop or OrderType.StopLimit ? request.Price : null,
                Side = request.Side == OrderSide.Buy ? "BUY" : "SELL",
                Type = request.Type switch
                {
                    OrderType.Limit => "LIMIT",
                    OrderType.Stop => "STOP",
                    OrderType.StopLimit => "STOP_LIMIT",
                    _ => "MARKET"
                },
                CreatedAtUtc = request.RequestedAtUtc,
                ParentBrokerOrderId = parentBrokerOrderId,
                Role = role,
                Transmit = role switch
                {
                    "BracketEntry" => false,
                    "ProtectiveStop" => false,
                    _ => true
                }
            };
        }

        private static string BuildOrderRawJson(OrderRequest request, OrderStatusDto brokerStatus, string role, string idempotencyKey, string? parentBrokerOrderId)
        {
            return JsonSerializer.Serialize(new
            {
                idempotencyKey,
                role,
                parentBrokerOrderId,
                request,
                brokerStatus,
                averageFillPrice = brokerStatus.AverageFillPrice,
                filledQuantity = brokerStatus.FilledQuantity,
                remainingQuantity = brokerStatus.RemainingQuantity,
                commission = brokerStatus.Commission,
                message = brokerStatus.Message
            }, JsonOptions);
        }

        private static string BuildOrderIdempotencyKey(OrderRequest request, string role, string? parentBrokerOrderId)
        {
            return $"{role}:{parentBrokerOrderId}:{request.Symbol}:{request.Side}:{request.Type}:{request.Quantity}:{request.Price}:{request.RequestedAtUtc:O}";
        }

        private static string BuildBracketIdempotencyKey(OrderRequest entry, OrderRequest stop, OrderRequest target)
        {
            return $"Bracket:{BuildOrderIdempotencyKey(entry, "Entry", null)}:{BuildOrderIdempotencyKey(stop, "Stop", null)}:{BuildOrderIdempotencyKey(target, "Target", null)}";
        }

        private static ManagedOrderResult ToManagedOrderResult(TradingBot.Persistence.OrderRecord record, bool submitted, bool duplicate)
        {
            return new ManagedOrderResult
            {
                OrderId = record.BrokerOrderId,
                BrokerOrderId = record.BrokerOrderId,
                Status = record.Status,
                Submitted = submitted,
                IsDuplicate = duplicate,
                Message = duplicate ? "Duplicate order submission prevented." : string.Empty
            };
        }

        private static ManagedOrderResult CopyDuplicateResult(ManagedOrderResult result, string message)
        {
            return new ManagedOrderResult
            {
                OrderId = result.OrderId,
                BrokerOrderId = result.BrokerOrderId,
                Status = result.Status,
                Submitted = false,
                IsDuplicate = true,
                FilledQuantity = result.FilledQuantity,
                RemainingQuantity = result.RemainingQuantity,
                AverageFillPrice = result.AverageFillPrice,
                Commission = result.Commission,
                ChildOrderIds = result.ChildOrderIds,
                Message = message
            };
        }

        private static ManagedOrderResult RejectedByGuard(TradingExecutionGuardResult guard)
        {
            return new ManagedOrderResult
            {
                Status = OrderStatus.Rejected,
                Submitted = false,
                Message = guard.Reason
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
