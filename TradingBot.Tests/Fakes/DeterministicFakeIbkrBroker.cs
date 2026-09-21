using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.Application.DTOs;
using TradingBot.Application.Exceptions;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Tests.Fakes
{
    public sealed class DeterministicFakeIbkrBroker :
        IIbkrAdapter,
        IIbkrConnectionService,
        IAccountService,
        IPositionService,
        IOrderExecutionService
    {
        private readonly object _sync = new();
        private readonly List<OrderStatusDto> _openOrders = new();
        private readonly List<OrderStatusDto> _allOrders = new();
        private readonly List<OrderRequestDto> _submittedRequests = new();
        private readonly List<OrderStatusDto> _executions = new();
        private readonly Dictionary<string, PositionDto> _positions = new(StringComparer.OrdinalIgnoreCase);
        private int _nextOrderId = 1;
        private int _nextExecutionId = 1;

        public DeterministicFakeIbkrBroker(string accountId = "DU123", bool isPaperAccount = true)
        {
            AccountId = accountId;
            IsPaperAccount = isPaperAccount;
        }

        public string AccountId { get; set; }
        public bool IsPaperAccount { get; set; }
        public decimal Cash { get; set; } = 100000m;
        public decimal NetLiquidation { get; set; } = 100000m;
        public decimal AvailableFunds { get; set; } = 100000m;
        public decimal BuyingPower { get; set; } = 100000m;
        public decimal MarginRequirement { get; set; }
        public FakeBrokerScenario Scenario { get; set; } = new();
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
        public bool IsConnected => Status == ConnectionStatus.Connected;
        public IReadOnlyList<OrderRequestDto> SubmittedRequests
        {
            get
            {
                lock (_sync)
                {
                    return _submittedRequests.ToList();
                }
            }
        }

        public IReadOnlyList<OrderStatusDto> Executions
        {
            get
            {
                lock (_sync)
                {
                    return _executions.ToList();
                }
            }
        }

        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
        public event Func<bool, Task>? ReadinessChanged;
        public event Func<MarketBar, Task>? MarketBarReceived;
#pragma warning disable CS0067
        public event Func<AccountInfo, Task>? AccountUpdated;
#pragma warning restore CS0067
        public event Func<PositionDto, Task>? PositionUpdated;
        public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
        public event Func<OrderStatusDto, Task>? OrderFilled;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Connected;
            await RaiseConnectionAsync().ConfigureAwait(false);
            if (ReadinessChanged != null) await ReadinessChanged(true).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Status = ConnectionStatus.Disconnected;
            await RaiseConnectionAsync().ConfigureAwait(false);
            if (ReadinessChanged != null) await ReadinessChanged(false).ConfigureAwait(false);
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => StartAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => StopAsync(cancellationToken);

        public async Task ForceConnectionLostAsync()
        {
            Status = ConnectionStatus.Reconnecting;
            await RaiseConnectionAsync().ConfigureAwait(false);
        }

        public Task ForceReconnectAsync() => StartAsync();

        public Task EmitMarketBarAsync(MarketBar bar)
        {
            return MarketBarReceived?.Invoke(bar) ?? Task.CompletedTask;
        }

        public async Task ReplayExecutionCallbacksAsync()
        {
            List<OrderStatusDto> executions;
            lock (_sync)
            {
                executions = _executions.ToList();
            }

            foreach (var execution in executions)
            {
                await RaiseFillAsync(execution).ConfigureAwait(false);
            }
        }

        public Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AccountInfo
            {
                AccountId = AccountId,
                NetLiquidation = NetLiquidation,
                AvailableFunds = AvailableFunds,
                BuyingPower = BuyingPower,
                MarginRequirement = MarginRequirement
            });
        }

        public void SetPosition(string symbol, decimal quantity, decimal averagePrice)
        {
            lock (_sync)
            {
                _positions[symbol] = new PositionDto
                {
                    AccountId = AccountId,
                    Symbol = symbol,
                    Quantity = quantity,
                    AveragePrice = averagePrice
                };
            }
        }

        public Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IEnumerable<PositionDto>>(_positions.Values.ToList());
            }
        }

        public async Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
        {
            if (Status != ConnectionStatus.Connected)
            {
                throw new InvalidOperationException("Fake IBKR broker is not connected.");
            }

            if (Scenario.OrderBehavior == FakeBrokerOrderBehavior.DisconnectBeforeSubmitResponse)
            {
                await ForceConnectionLostAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Disconnected before broker accepted order.");
            }

            var orderId = $"FAKE-{_nextOrderId++:000000}";
            var accepted = BuildStatus(orderId, request, "Submitted", 0m, request.Quantity, null, null, null, "Submitted");
            lock (_sync)
            {
                _submittedRequests.Add(request);
                _allOrders.Add(accepted);
                _openOrders.Add(accepted);
            }

            if (Scenario.OrderBehavior == FakeBrokerOrderBehavior.DisconnectAfterBrokerAcceptedOrder)
            {
                await ForceConnectionLostAsync().ConfigureAwait(false);
                throw new BrokerOrderStateUnknownException("Disconnected after broker accepted order but before confirmation reached application.");
            }

            if (IsRejectBehavior(Scenario.OrderBehavior))
            {
                var rejected = BuildStatus(orderId, request, "Rejected", 0m, request.Quantity, null, null, null, RejectionMessage());
                ReplaceOpenOrder(rejected, keepOpen: false);
                return rejected;
            }

            if (Scenario.OrderBehavior == FakeBrokerOrderBehavior.NeverReturnOrderStatus)
            {
                throw new TimeoutException("Fake broker did not return order status.");
            }

            if (Scenario.OrderBehavior == FakeBrokerOrderBehavior.FillImmediately)
            {
                return await ApplyFillsAsync(orderId, request, new[] { request.Quantity }, Scenario.FillPrices, disconnectDuringFill: false).ConfigureAwait(false);
            }

            if (Scenario.OrderBehavior == FakeBrokerOrderBehavior.PartialFill
                || Scenario.OrderBehavior == FakeBrokerOrderBehavior.DisconnectDuringPartialFill)
            {
                return await ApplyFillsAsync(
                    orderId,
                    request,
                    Scenario.FillQuantities,
                    Scenario.FillPrices,
                    disconnectDuringFill: Scenario.OrderBehavior == FakeBrokerOrderBehavior.DisconnectDuringPartialFill).ConfigureAwait(false);
            }

            return accepted;
        }

        public async Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            OrderStatusDto? cancelled = null;
            lock (_sync)
            {
                var existing = _openOrders.FirstOrDefault(o => string.Equals(o.BrokerOrderId ?? o.OrderId, orderId, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    cancelled = new OrderStatusDto
                    {
                        OrderId = existing.OrderId,
                        BrokerOrderId = existing.BrokerOrderId,
                        Status = "Cancelled",
                        FilledQuantity = existing.FilledQuantity,
                        RemainingQuantity = existing.RemainingQuantity,
                        AverageFillPrice = existing.AverageFillPrice,
                        TimestampUtc = DateTime.UtcNow,
                        Message = "Fake broker cancelled remaining quantity."
                    };
                    _openOrders.Remove(existing);
                    _allOrders.Add(cancelled);
                }
            }

            if (cancelled != null)
            {
                await RaiseOrderStatusAsync(cancelled).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        public Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult<IEnumerable<OrderStatusDto>>(_openOrders.ToList());
            }
        }

        private async Task<OrderStatusDto> ApplyFillsAsync(string orderId, OrderRequestDto request, IReadOnlyList<decimal> fillQuantities, IReadOnlyList<decimal> fillPrices, bool disconnectDuringFill)
        {
            var totalFilled = 0m;
            var weightedValue = 0m;
            var remaining = request.Quantity;
            OrderStatusDto last = BuildStatus(orderId, request, "Submitted", 0m, remaining, null, null, null, "Submitted");

            for (var i = 0; i < fillQuantities.Count && remaining > 0m; i++)
            {
                var fillQty = Math.Min(fillQuantities[i], remaining);
                var fillPrice = fillPrices.Count > i ? fillPrices[i] : fillPrices.LastOrDefault();
                if (fillPrice <= 0m) fillPrice = request.LimitPrice ?? 100m;

                totalFilled += fillQty;
                weightedValue += fillQty * fillPrice;
                remaining -= fillQty;

                ApplyPosition(request, fillQty, fillPrice);

                var executionId = $"EXEC-{_nextExecutionId++:000000}";
                last = BuildStatus(
                    orderId,
                    request,
                    remaining == 0m ? "Filled" : "PartiallyFilled",
                    totalFilled,
                    remaining,
                    weightedValue / totalFilled,
                    fillPrice,
                    executionId,
                    "Fake broker fill.");

                ReplaceOpenOrder(last, keepOpen: remaining > 0m);
                lock (_sync)
                {
                    _executions.Add(last);
                }

                if (disconnectDuringFill)
                {
                    await ForceConnectionLostAsync().ConfigureAwait(false);
                    throw new BrokerOrderStateUnknownException("Disconnected during partial fill; order state requires reconciliation.");
                }
            }

            return last;
        }

        private void ApplyPosition(OrderRequestDto request, decimal fillQty, decimal fillPrice)
        {
            lock (_sync)
            {
                _positions.TryGetValue(request.Symbol, out var existing);
                var oldQty = existing?.Quantity ?? 0m;
                var oldAvg = existing?.AveragePrice ?? 0m;
                var signedFill = string.Equals(request.Side, "BUY", StringComparison.OrdinalIgnoreCase) ? fillQty : -fillQty;
                var newQty = oldQty + signedFill;
                var newAvg = newQty == 0m
                    ? 0m
                    : signedFill > 0m
                        ? ((oldQty * oldAvg) + (fillQty * fillPrice)) / newQty
                        : oldAvg;

                _positions[request.Symbol] = new PositionDto
                {
                    AccountId = AccountId,
                    Symbol = request.Symbol,
                    Quantity = newQty,
                    AveragePrice = newAvg
                };
            }

            _ = PositionUpdated?.Invoke(new PositionDto
            {
                AccountId = AccountId,
                Symbol = request.Symbol,
                Quantity = fillQty,
                AveragePrice = fillPrice
            });
        }

        private OrderStatusDto BuildStatus(string orderId, OrderRequestDto request, string status, decimal filled, decimal remaining, decimal? averagePrice, decimal? lastPrice, string? executionId, string? message)
        {
            return new OrderStatusDto
            {
                OrderId = orderId,
                BrokerOrderId = orderId,
                Status = status,
                FilledQuantity = filled,
                RemainingQuantity = remaining,
                AverageFillPrice = averagePrice,
                LastFillPrice = lastPrice,
                Commission = executionId == null ? null : Scenario.CommissionPerFill,
                BrokerExecutionId = executionId,
                TimestampUtc = DateTime.UtcNow,
                Message = message ?? request.Symbol
            };
        }

        private void ReplaceOpenOrder(OrderStatusDto status, bool keepOpen)
        {
            lock (_sync)
            {
                var existing = _openOrders.FirstOrDefault(o => o.OrderId == status.OrderId);
                if (existing != null) _openOrders.Remove(existing);
                if (keepOpen) _openOrders.Add(status);
                _allOrders.Add(status);
            }
        }

        private Task RaiseConnectionAsync() => ConnectionStatusChanged?.Invoke(Status) ?? Task.CompletedTask;
        private Task RaiseOrderStatusAsync(OrderStatusDto status) => OrderStatusUpdated?.Invoke(status) ?? Task.CompletedTask;
        private Task RaiseFillAsync(OrderStatusDto status) => OrderFilled?.Invoke(status) ?? Task.CompletedTask;

        private static bool IsRejectBehavior(FakeBrokerOrderBehavior behavior)
        {
            return behavior is FakeBrokerOrderBehavior.RejectOrder
                or FakeBrokerOrderBehavior.RejectForInsufficientBuyingPower
                or FakeBrokerOrderBehavior.RejectForInvalidPrice
                or FakeBrokerOrderBehavior.RejectBecauseMarketClosed;
        }

        private string RejectionMessage()
        {
            return Scenario.OrderBehavior switch
            {
                FakeBrokerOrderBehavior.RejectForInsufficientBuyingPower => "Insufficient buying power.",
                FakeBrokerOrderBehavior.RejectForInvalidPrice => "Invalid price.",
                FakeBrokerOrderBehavior.RejectBecauseMarketClosed => "Market is closed.",
                _ => Scenario.RejectionReason
            };
        }
    }
}
