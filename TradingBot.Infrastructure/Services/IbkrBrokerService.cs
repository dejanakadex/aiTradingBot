#if IBKR_API_AVAILABLE
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using IBApi;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Exceptions;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public sealed class IbkrBrokerService : DefaultEWrapper, IIbkrAdapter, IOrderExecutionService, IOrderModificationService, IAccountService, IPositionService, IMarketDataService, IDisposable
    {
        private static readonly TimeSpan BrokerResponseTimeout = TimeSpan.FromSeconds(10);
        private static readonly string[] AccountSummaryTags = ["NetLiquidation", "AvailableFunds", "BuyingPower", "MaintMarginReq"];

        private readonly IbkrOptions _options;
        private readonly ILogger<IbkrBrokerService> _logger;
        private readonly object _clientLock = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<OrderStatusDto>> _pendingOrderConfirmations = new();
        private readonly ConcurrentDictionary<int, OrderStatusDto> _openOrders = new();
        private readonly ConcurrentDictionary<string, PositionDto> _positions = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, AccountSummaryRequest> _accountRequests = new();
        private readonly ConcurrentDictionary<int, PositionsRequest> _positionRequests = new();
        private readonly ConcurrentDictionary<int, OpenOrdersRequest> _openOrdersRequests = new();
        private readonly ConcurrentDictionary<int, HistoricalBarsRequest> _historicalRequests = new();
        private readonly ConcurrentDictionary<int, StreamingSubscription> _subscriptions = new();
        private readonly ConcurrentDictionary<int, LevelOneSubscription> _levelOneSubscriptions = new();
        private readonly ConcurrentDictionary<string, decimal> _commissionsByExecutionId = new(StringComparer.OrdinalIgnoreCase);

        private EReaderSignal? _signal;
        private EClientSocket? _client;
        private CancellationTokenSource? _readerLoopCts;
        private Task? _readerLoop;
        private int _nextOrderId;
        private int _nextRequestId = 10_000;

        public IbkrBrokerService(IbkrOptions options, ILogger<IbkrBrokerService> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsConnected => _client?.IsConnected() == true;

        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
        public event Func<bool, Task>? ReadinessChanged;
        public event Func<MarketBar, Task>? MarketBarReceived;
        public event Func<CanonicalMarketDataEvent, Task>? MarketDataEventReceived;
        public event Func<AccountInfo, Task>? AccountUpdated;
        public event Func<PositionDto, Task>? PositionUpdated;
        public event Func<OrderStatusDto, Task>? OrderStatusUpdated;
        public event Func<OrderStatusDto, Task>? OrderFilled;

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            lock (_clientLock)
            {
                if (_client?.IsConnected() == true)
                {
                    return;
                }

                _signal = new EReaderMonitorSignal();
                _client = new EClientSocket(this, _signal);
                _readerLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            await PublishConnectionStatusAsync(ConnectionStatus.Connecting).ConfigureAwait(false);
            _logger.LogInformation("Connecting to IBKR at {Host}:{Port} with clientId={ClientId}", _options.Host, _options.Port, _options.ClientId);

            await Task.Run(() => _client!.eConnect(_options.Host, _options.Port, _options.ClientId), cancellationToken).ConfigureAwait(false);

            if (!IsConnected)
            {
                await PublishConnectionStatusAsync(ConnectionStatus.Failed).ConfigureAwait(false);
                throw new InvalidOperationException("IBKR connection was not established. Check TWS/Gateway API settings, host, port, and client id.");
            }

            var reader = new EReader(_client!, _signal!);
            reader.Start();
            _readerLoop = Task.Run(() => ProcessMessagesAsync(reader, _signal!, _readerLoopCts!.Token), CancellationToken.None);

            _client!.reqManagedAccts();
            _client.reqIds(-1);
            _client.reqAllOpenOrders();

            await PublishConnectionStatusAsync(ConnectionStatus.Connected).ConfigureAwait(false);
            await PublishReadinessAsync(_nextOrderId > 0).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            EClientSocket? client;
            Task? readerLoop;

            lock (_clientLock)
            {
                client = _client;
                readerLoop = _readerLoop;
                _readerLoopCts?.Cancel();
            }

            try
            {
                client?.eDisconnect();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while disconnecting IBKR client.");
            }

            if (readerLoop != null)
            {
                try
                {
                    await readerLoop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogDebug("IBKR reader loop did not stop before timeout.");
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("IBKR reader loop stop wait canceled.");
                }
            }

            lock (_clientLock)
            {
                _readerLoopCts?.Dispose();
                _readerLoopCts = null;
                _readerLoop = null;
                _client = null;
                _signal = null;
                _nextOrderId = 0;
            }

            await PublishReadinessAsync(false).ConfigureAwait(false);
            await PublishConnectionStatusAsync(ConnectionStatus.Disconnected).ConfigureAwait(false);
        }

        public async Task<OrderStatusDto> SubmitOrderAsync(OrderRequestDto request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var client = GetConnectedClient();
            var orderId = GetNextOrderId();
            var contract = BuildStockContract(request.Symbol);
            var order = BuildOrder(orderId, request);
            var confirmation = NewTcs<OrderStatusDto>();

            if (!_pendingOrderConfirmations.TryAdd(orderId, confirmation))
            {
                throw new InvalidOperationException($"Duplicate pending IBKR order id {orderId}.");
            }

            try
            {
                _logger.LogInformation(
                    "Submitting IBKR order {OrderId}: {Side} {Quantity} {Symbol} {Type} limit={LimitPrice} stop={StopPrice} parent={ParentBrokerOrderId} transmit={Transmit}",
                    orderId,
                    request.Side,
                    request.Quantity,
                    request.Symbol,
                    request.Type,
                    request.LimitPrice,
                    request.StopPrice,
                    request.ParentBrokerOrderId,
                    request.Transmit);

                client.placeOrder(orderId, contract, order);

                try
                {
                    return await confirmation.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new BrokerOrderStateUnknownException($"IBKR did not confirm order {orderId} before timeout. Reconciliation is required before retrying.", ex);
                }
            }
            finally
            {
                _pendingOrderConfirmations.TryRemove(orderId, out _);
            }
        }

        public Task<bool> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            if (!int.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var brokerOrderId))
            {
                return Task.FromResult(false);
            }

            var client = GetConnectedClient();
            client.cancelOrder(brokerOrderId, new OrderCancel());
            return Task.FromResult(true);
        }

        public async Task<OrderStatusDto> ModifyOrderAsync(string brokerOrderId, OrderRequestDto request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (!int.TryParse(brokerOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderId) || orderId <= 0)
            {
                throw new ArgumentException("A valid numeric IBKR broker order id is required for order modification.", nameof(brokerOrderId));
            }

            var client = GetConnectedClient();
            var contract = BuildStockContract(request.Symbol);
            var order = BuildOrder(orderId, request);
            var confirmation = NewTcs<OrderStatusDto>();

            if (!_pendingOrderConfirmations.TryAdd(orderId, confirmation))
            {
                throw new InvalidOperationException($"Duplicate pending IBKR order modification id {orderId}.");
            }

            try
            {
                _logger.LogInformation(
                    "Modifying IBKR order {OrderId}: {Side} {Quantity} {Symbol} {Type} limit={LimitPrice} stop={StopPrice}",
                    orderId,
                    request.Side,
                    request.Quantity,
                    request.Symbol,
                    request.Type,
                    request.LimitPrice,
                    request.StopPrice);

                client.placeOrder(orderId, contract, order);

                try
                {
                    return await confirmation.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new BrokerOrderStateUnknownException($"IBKR did not confirm order modification {orderId} before timeout. Reconciliation is required.", ex);
                }
            }
            finally
            {
                _pendingOrderConfirmations.TryRemove(orderId, out _);
            }
        }

        public async Task<IEnumerable<OrderStatusDto>> GetOpenOrdersAsync(CancellationToken cancellationToken = default)
        {
            var client = GetConnectedClient();
            var requestId = GetNextRequestId();
            var request = new OpenOrdersRequest();
            _openOrdersRequests[requestId] = request;

            client.reqAllOpenOrders();

            try
            {
                return await request.Completion.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _openOrdersRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<AccountInfo> GetAccountInfoAsync(string accountId, CancellationToken cancellationToken = default)
        {
            var client = GetConnectedClient();
            var requestId = GetNextRequestId();
            var request = new AccountSummaryRequest(accountId);
            _accountRequests[requestId] = request;

            client.reqAccountSummary(requestId, "All", string.Join(",", AccountSummaryTags));

            try
            {
                return await request.Completion.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryBrokerCall(() => client.cancelAccountSummary(requestId), "cancel account summary");
                _accountRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<IEnumerable<PositionDto>> GetPositionsAsync(string accountId, CancellationToken cancellationToken = default)
        {
            var client = GetConnectedClient();
            var requestId = GetNextRequestId();
            var request = new PositionsRequest(accountId);
            _positionRequests[requestId] = request;

            client.reqPositions();

            try
            {
                return await request.Completion.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryBrokerCall(client.cancelPositions, "cancel positions");
                _positionRequests.TryRemove(requestId, out _);
            }
        }

        public async Task<IEnumerable<MarketBar>> GetHistoricalBarsAsync(string symbol, string timeframe, int count, CancellationToken cancellationToken = default)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "count must be greater than zero.");
            var client = GetConnectedClient();
            var requestId = GetNextRequestId();
            var request = new HistoricalBarsRequest(symbol, NormalizeTimeframe(timeframe));
            _historicalRequests[requestId] = request;

            client.reqHistoricalData(
                requestId,
                BuildStockContract(symbol),
                string.Empty,
                BuildDuration(timeframe, count),
                ToIbkrBarSize(timeframe),
                "TRADES",
                1,
                2,
                false,
                []);

            try
            {
                return await request.Completion.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryBrokerCall(() => client.cancelHistoricalData(requestId), "cancel historical data");
                _historicalRequests.TryRemove(requestId, out _);
            }
        }

        public Task<IMarketDataSubscription> SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken = default)
        {
            var client = GetConnectedClient();
            var requestId = GetNextRequestId();
            var normalizedTimeframe = NormalizeTimeframe(timeframe);
            int? levelOneRequestId = null;
            if (normalizedTimeframe == "1m")
            {
                levelOneRequestId = GetNextRequestId();
                _levelOneSubscriptions[levelOneRequestId.Value] = new LevelOneSubscription(symbol);
                client.reqMktData(levelOneRequestId.Value, BuildStockContract(symbol), string.Empty, false, false, []);
            }
            var channel = Channel.CreateBounded<MarketBar>(new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = true
            });
            var subscription = new StreamingSubscription(requestId, channel, () =>
            {
                _subscriptions.TryRemove(requestId, out _);
                TryBrokerCall(() => client.cancelHistoricalData(requestId), "cancel streaming historical data");
                if (levelOneRequestId.HasValue)
                {
                    _levelOneSubscriptions.TryRemove(levelOneRequestId.Value, out _);
                    TryBrokerCall(() => client.cancelMktData(levelOneRequestId.Value), "cancel level-one market data");
                }
            });
            subscription.SetContext(symbol, normalizedTimeframe);

            _subscriptions[requestId] = subscription;

            client.reqHistoricalData(
                requestId,
                BuildStockContract(symbol),
                string.Empty,
                BuildDuration(timeframe, 50),
                ToIbkrBarSize(timeframe),
                "TRADES",
                1,
                2,
                true,
                []);

            return Task.FromResult<IMarketDataSubscription>(subscription);
        }

        public override void nextValidId(int orderId)
        {
            if (orderId <= 0) return;
            Interlocked.Exchange(ref _nextOrderId, orderId);
            _ = PublishReadinessAsync(true);
        }

        public override void managedAccounts(string accountsList)
        {
            _logger.LogInformation("IBKR managed accounts received: {ManagedAccounts}", accountsList);
        }

        public override void connectionClosed()
        {
            _logger.LogWarning("IBKR connection closed.");
            _ = PublishReadinessAsync(false);
            _ = PublishConnectionStatusAsync(ConnectionStatus.Disconnected);
        }

        public override void error(Exception e)
        {
            _logger.LogError(e, "IBKR exception callback.");
            _ = PublishReadinessAsync(false);
            _ = PublishConnectionStatusAsync(ConnectionStatus.Failed);
        }

        public override void error(string str)
        {
            _logger.LogError("IBKR error callback: {Message}", str);
        }

        public override void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            _logger.LogWarning("IBKR error id={Id} code={ErrorCode}: {Message}", id, errorCode, errorMsg);

            if (_pendingOrderConfirmations.TryGetValue(id, out var orderTcs))
            {
                orderTcs.TrySetResult(new OrderStatusDto
                {
                    OrderId = id.ToString(CultureInfo.InvariantCulture),
                    BrokerOrderId = id.ToString(CultureInfo.InvariantCulture),
                    Status = "Rejected",
                    TimestampUtc = DateTime.UtcNow,
                    Message = string.IsNullOrWhiteSpace(advancedOrderRejectJson)
                        ? errorMsg
                        : $"{errorMsg}; advancedOrderRejectJson present"
                });
            }

            if (errorCode is 1100 or 1300)
            {
                _ = PublishReadinessAsync(false);
                _ = PublishConnectionStatusAsync(ConnectionStatus.Disconnected);
            }
            else if (errorCode is 1101 or 1102)
            {
                _ = PublishConnectionStatusAsync(ConnectionStatus.Connected);
            }
        }

        public override void accountSummary(int reqId, string account, string tag, string value, string currency)
        {
            if (!_accountRequests.TryGetValue(reqId, out var request)) return;
            request.Record(account, tag, value);
        }

        public override void accountSummaryEnd(int reqId)
        {
            CompleteAccountSummaryRequest(reqId);
        }

        public override void accountSummaryProtoBuf(IBApi.protobuf.AccountSummary accountSummaryProto)
        {
            if (!_accountRequests.TryGetValue(accountSummaryProto.ReqId, out var request)) return;
            request.Record(accountSummaryProto.Account, accountSummaryProto.Tag, accountSummaryProto.Value);
        }

        public override void accountSummaryEndProtoBuf(IBApi.protobuf.AccountSummaryEnd accountSummaryEndProto)
        {
            CompleteAccountSummaryRequest(accountSummaryEndProto.ReqId);
        }

        public override void position(string account, Contract contract, decimal pos, double avgCost)
        {
            RecordPosition(new PositionDto
            {
                AccountId = account,
                Symbol = contract.Symbol ?? string.Empty,
                Quantity = pos,
                AveragePrice = ToDecimal(avgCost)
            });
        }

        public override void positionProtoBuf(IBApi.protobuf.Position positionProto)
        {
            RecordPosition(new PositionDto
            {
                AccountId = positionProto.Account ?? string.Empty,
                Symbol = positionProto.Contract?.Symbol ?? string.Empty,
                Quantity = ToDecimal(positionProto.Position_),
                AveragePrice = ToDecimal(positionProto.AvgCost)
            });
        }

        public override void positionEnd()
        {
            CompletePositionRequests();
        }

        public override void positionEndProtoBuf(IBApi.protobuf.PositionEnd positionEndProto)
        {
            CompletePositionRequests();
        }

        public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        {
            RecordOpenOrder(new OrderStatusDto
            {
                OrderId = orderId.ToString(CultureInfo.InvariantCulture),
                BrokerOrderId = orderId.ToString(CultureInfo.InvariantCulture),
                Status = "Submitted",
                TimestampUtc = DateTime.UtcNow,
                RemainingQuantity = order.TotalQuantity,
                Message = orderState?.Status
            }, orderId);
        }

        public override void openOrderProtoBuf(IBApi.protobuf.OpenOrder openOrderProto)
        {
            RecordOpenOrder(new OrderStatusDto
            {
                OrderId = openOrderProto.OrderId.ToString(CultureInfo.InvariantCulture),
                BrokerOrderId = openOrderProto.OrderId.ToString(CultureInfo.InvariantCulture),
                Status = "Submitted",
                TimestampUtc = DateTime.UtcNow,
                RemainingQuantity = ToDecimal(openOrderProto.Order?.TotalQuantity),
                Message = openOrderProto.OrderState?.Status
            }, openOrderProto.OrderId);
        }

        public override void openOrderEnd()
        {
            CompleteOpenOrderRequests();
        }

        public override void openOrdersEndProtoBuf(IBApi.protobuf.OpenOrdersEnd openOrdersEndProto)
        {
            CompleteOpenOrderRequests();
        }

        public override void orderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            HandleOrderStatus(orderId, status, filled, remaining, avgFillPrice, lastFillPrice, whyHeld);
        }

        public override void orderStatusProtoBuf(IBApi.protobuf.OrderStatus orderStatusProto)
        {
            HandleOrderStatus(
                orderStatusProto.OrderId,
                orderStatusProto.Status,
                ToDecimal(orderStatusProto.Filled),
                ToDecimal(orderStatusProto.Remaining),
                orderStatusProto.AvgFillPrice,
                orderStatusProto.LastFillPrice,
                orderStatusProto.WhyHeld);
        }

        private void CompleteAccountSummaryRequest(int reqId)
        {
            if (!_accountRequests.TryGetValue(reqId, out var request)) return;

            var account = request.Build();
            request.Completion.TrySetResult(account);
            _ = InvokeAccountUpdatedAsync(account);
        }

        private void RecordPosition(PositionDto dto)
        {
            _positions[$"{dto.AccountId}:{dto.Symbol}"] = dto;
            foreach (var request in _positionRequests.Values)
            {
                request.Record(dto);
            }

            _ = InvokePositionUpdatedAsync(dto);
        }

        private void CompletePositionRequests()
        {
            foreach (var request in _positionRequests.Values)
            {
                request.Completion.TrySetResult(request.Build());
            }
        }

        private void RecordOpenOrder(OrderStatusDto dto, int orderId)
        {
            _openOrders[orderId] = dto;
            foreach (var request in _openOrdersRequests.Values)
            {
                request.Record(dto);
            }

            CompletePendingOrder(orderId, dto);
            _ = InvokeOrderStatusUpdatedAsync(dto);
        }

        private void CompleteOpenOrderRequests()
        {
            foreach (var request in _openOrdersRequests.Values)
            {
                request.Completion.TrySetResult(_openOrders.Values.ToArray());
            }
        }

        private void HandleOrderStatus(int orderId, string status, decimal filled, decimal remaining, double avgFillPrice, double lastFillPrice, string whyHeld)
        {
            var dto = new OrderStatusDto
            {
                OrderId = orderId.ToString(CultureInfo.InvariantCulture),
                BrokerOrderId = orderId.ToString(CultureInfo.InvariantCulture),
                Status = NormalizeOrderStatus(status, filled, remaining),
                TimestampUtc = DateTime.UtcNow,
                FilledQuantity = filled,
                RemainingQuantity = remaining,
                AverageFillPrice = ToNullableDecimal(avgFillPrice),
                LastFillPrice = ToNullableDecimal(lastFillPrice),
                Message = whyHeld
            };

            if (IsOpenStatus(dto.Status))
            {
                _openOrders[orderId] = dto;
            }
            else
            {
                _openOrders.TryRemove(orderId, out _);
            }

            CompletePendingOrder(orderId, dto);
            _ = InvokeOrderStatusUpdatedAsync(dto);
            if (filled > 0 && remaining == 0)
            {
                _ = InvokeOrderFilledAsync(dto);
            }
        }

        public override void execDetails(int reqId, Contract contract, Execution execution)
        {
            var dto = new OrderStatusDto
            {
                OrderId = execution.OrderId.ToString(CultureInfo.InvariantCulture),
                BrokerOrderId = execution.OrderId.ToString(CultureInfo.InvariantCulture),
                Status = "Filled",
                TimestampUtc = ParseIbkrExecutionTime(execution.Time),
                FilledQuantity = execution.CumQty,
                RemainingQuantity = 0m,
                AverageFillPrice = ToNullableDecimal(execution.AvgPrice),
                LastFillPrice = ToNullableDecimal(execution.Price),
                Commission = _commissionsByExecutionId.TryGetValue(execution.ExecId ?? string.Empty, out var commission) ? commission : null,
                BrokerExecutionId = execution.ExecId,
                Message = execution.Side
            };

            _ = InvokeOrderFilledAsync(dto);
        }

        public override void commissionAndFeesReport(CommissionAndFeesReport commissionAndFeesReport)
        {
            if (string.IsNullOrWhiteSpace(commissionAndFeesReport.ExecId)) return;
            _commissionsByExecutionId[commissionAndFeesReport.ExecId] = ToDecimal(commissionAndFeesReport.CommissionAndFees);
        }

        public override void historicalData(int reqId, Bar bar)
        {
            if (_historicalRequests.TryGetValue(reqId, out var historicalRequest))
            {
                historicalRequest.Record(ToMarketBar(historicalRequest.Symbol, historicalRequest.Timeframe, bar));
            }

            if (_subscriptions.TryGetValue(reqId, out var subscription))
            {
                subscription.RecordBackfillBar();
            }
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            if (!_levelOneSubscriptions.TryGetValue(tickerId, out var subscription)
                || double.IsNaN(price)
                || double.IsInfinity(price)
                || price <= 0d)
            {
                return;
            }

            var kind = field switch
            {
                1 => MarketDataEventKind.Bid,
                2 => MarketDataEventKind.Ask,
                4 => MarketDataEventKind.Trade,
                _ => (MarketDataEventKind?)null
            };
            if (!kind.HasValue) return;

            var now = DateTime.UtcNow;
            var sequence = subscription.NextSequence(kind.Value);
            var marketEvent = new CanonicalMarketDataEvent
            {
                EventId = $"IBKR:{tickerId}:{kind.Value}:{sequence}",
                InstrumentId = subscription.Symbol,
                Symbol = subscription.Symbol,
                Kind = kind.Value,
                EventTimeUtc = now,
                ReceivedTimeUtc = now,
                Source = "IBKR.Level1",
                Sequence = sequence,
                IsFinal = true,
                Price = Convert.ToDecimal(price, CultureInfo.InvariantCulture)
            };
            _ = InvokeMarketDataEventReceivedAsync(marketEvent);
        }

        public override void historicalDataUpdate(int reqId, Bar bar)
        {
            if (_subscriptions.TryGetValue(reqId, out var subscription))
            {
                if (!subscription.BackfillCompleted)
                {
                    _logger.LogDebug(
                        "Ignoring IBKR historical update before backfill completion for {Symbol} {Timeframe}.",
                        subscription.Symbol,
                        subscription.Timeframe);
                    return;
                }

                var marketBar = ToMarketBar(subscription.Symbol, subscription.Timeframe, bar);
                var completedBar = subscription.AcceptLiveUpdate(marketBar);
                if (completedBar == null)
                {
                    _logger.LogDebug(
                        "Buffered IBKR streaming update for current {Symbol} {Timeframe} bar at {TimestampUtc}; waiting for next bar before publishing.",
                        marketBar.Symbol,
                        marketBar.Timeframe,
                        marketBar.TimestampUtc);
                    return;
                }

                PublishMarketBar(subscription, completedBar);
            }
        }

        public override void historicalDataEnd(int reqId, string start, string end)
        {
            if (_historicalRequests.TryGetValue(reqId, out var request))
            {
                request.Completion.TrySetResult(request.Bars.ToArray());
            }

            if (_subscriptions.TryGetValue(reqId, out var subscription))
            {
                subscription.MarkBackfillCompleted();
                _logger.LogInformation(
                    "IBKR streaming historical backfill completed for {Symbol} {Timeframe}; completed live bars may now enter the trading pipeline.",
                    subscription.Symbol,
                    subscription.Timeframe);
            }
        }

        private async Task ProcessMessagesAsync(EReader reader, EReaderSignal signal, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                try
                {
                    signal.waitForSignal();
                    reader.processMsgs();
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "IBKR reader loop failed.");
                    await PublishReadinessAsync(false).ConfigureAwait(false);
                    await PublishConnectionStatusAsync(ConnectionStatus.Failed).ConfigureAwait(false);
                    return;
                }
            }
        }

        private EClientSocket GetConnectedClient()
        {
            var client = _client;
            if (client?.IsConnected() != true)
            {
                throw new InvalidOperationException("IBKR is not connected.");
            }

            return client;
        }

        private int GetNextOrderId()
        {
            var id = Interlocked.Increment(ref _nextOrderId) - 1;
            if (id <= 0)
            {
                throw new InvalidOperationException("IBKR has not provided a valid order id yet.");
            }

            return id;
        }

        private int GetNextRequestId() => Interlocked.Increment(ref _nextRequestId);

        private static Contract BuildStockContract(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol is required", nameof(symbol));

            return new Contract
            {
                Symbol = symbol.Trim().ToUpperInvariant(),
                SecType = "STK",
                Exchange = "SMART",
                Currency = "USD"
            };
        }

        private Order BuildOrder(int orderId, OrderRequestDto request)
        {
            var order = new Order
            {
                OrderId = orderId,
                Action = request.Side.Trim().ToUpperInvariant(),
                TotalQuantity = request.Quantity,
                OrderType = ToIbkrOrderType(request.Type),
                Tif = "DAY",
                Account = _options.AccountId ?? string.Empty,
                Transmit = request.Transmit ?? true,
                OrderRef = BuildOrderRef(request)
            };

            if (int.TryParse(request.ParentBrokerOrderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentOrderId))
            {
                order.ParentId = parentOrderId;
            }

            if (order.OrderType is "LMT" or "STP LMT")
            {
                order.LmtPrice = (double)(request.LimitPrice ?? 0m);
            }

            if (order.OrderType is "STP" or "STP LMT")
            {
                order.AuxPrice = (double)(request.StopPrice ?? request.LimitPrice ?? 0m);
            }

            return order;
        }

        private static string BuildOrderRef(OrderRequestDto request)
        {
            var created = request.CreatedAtUtc == default ? DateTime.UtcNow : request.CreatedAtUtc;
            var role = string.IsNullOrWhiteSpace(request.Role) ? "Order" : request.Role;
            return $"TradingBot:{role}:{created:yyyyMMddHHmmssfff}";
        }

        private static string ToIbkrOrderType(string type)
        {
            return type.Trim().ToUpperInvariant() switch
            {
                "LIMIT" or "LMT" => "LMT",
                "STOP" or "STP" => "STP",
                "STOP_LIMIT" or "STP_LMT" or "STP LMT" => "STP LMT",
                "MARKET" or "MKT" => "MKT",
                _ => throw new ArgumentException($"Unsupported IBKR order type '{type}'.")
            };
        }

        private static string NormalizeOrderStatus(string status, decimal filled, decimal remaining)
        {
            if (filled > 0 && remaining > 0) return "PartiallyFilled";
            return status;
        }

        private static bool IsOpenStatus(string status)
        {
            return status.Equals("Submitted", StringComparison.OrdinalIgnoreCase)
                || status.Equals("PreSubmitted", StringComparison.OrdinalIgnoreCase)
                || status.Equals("PendingSubmit", StringComparison.OrdinalIgnoreCase)
                || status.Equals("PartiallyFilled", StringComparison.OrdinalIgnoreCase);
        }

        private void CompletePendingOrder(int orderId, OrderStatusDto status)
        {
            if (_pendingOrderConfirmations.TryGetValue(orderId, out var tcs))
            {
                tcs.TrySetResult(status);
            }
        }

        private static string NormalizeTimeframe(string timeframe)
        {
            return timeframe.Trim().ToLowerInvariant() switch
            {
                "1m" or "1 min" or "1 minute" => "1m",
                "5m" or "5 mins" or "5 minutes" => "5m",
                "15m" or "15 mins" or "15 minutes" => "15m",
                _ => throw new ArgumentException($"Unsupported timeframe '{timeframe}'.")
            };
        }

        private static string ToIbkrBarSize(string timeframe)
        {
            return NormalizeTimeframe(timeframe) switch
            {
                "1m" => "1 min",
                "5m" => "5 mins",
                "15m" => "15 mins",
                _ => "1 min"
            };
        }

        private static string BuildDuration(string timeframe, int count)
        {
            var secondsPerBar = NormalizeTimeframe(timeframe) switch
            {
                "1m" => 60,
                "5m" => 300,
                "15m" => 900,
                _ => 60
            };

            return $"{Math.Max(secondsPerBar * (count + 2), secondsPerBar)} S";
        }

        private void PublishMarketBar(StreamingSubscription subscription, MarketBar marketBar)
        {
            if (!subscription.Channel.Writer.TryWrite(marketBar))
            {
                _logger.LogWarning("Dropped IBKR market bar for {Symbol} {Timeframe}; subscription channel is full.", subscription.Symbol, subscription.Timeframe);
            }
        }

        private static MarketBar ToMarketBar(string symbol, string timeframe, Bar bar)
        {
            return new MarketBar
            {
                Symbol = symbol,
                Timeframe = timeframe,
                TimestampUtc = ParseIbkrBarTime(bar.Time),
                ReceivedTimeUtc = DateTime.UtcNow,
                Source = "IBKR",
                IsFinal = true,
                Open = ToDecimal(bar.Open),
                High = ToDecimal(bar.High),
                Low = ToDecimal(bar.Low),
                Close = ToDecimal(bar.Close),
                Volume = bar.Volume
            };
        }

        private static DateTime ParseIbkrBarTime(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            }

            if (DateTime.TryParseExact(value, "yyyyMMdd  HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exactWithDoubleSpace))
            {
                return exactWithDoubleSpace.ToUniversalTime();
            }

            if (DateTime.TryParseExact(value, "yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            {
                return exact.ToUniversalTime();
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return DateTime.UtcNow;
        }

        private static DateTime ParseIbkrExecutionTime(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DateTime.UtcNow : ParseIbkrBarTime(value);
        }

        private static decimal ToDecimal(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value == double.MaxValue) return 0m;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        private static decimal ToDecimal(string? value)
        {
            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0m;
        }

        private static decimal? ToNullableDecimal(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value == double.MaxValue || value == 0d) return null;
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }

        private static TaskCompletionSource<T> NewTcs<T>()
        {
            return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void TryBrokerCall(Action action, string operation)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IBKR cleanup operation failed: {Operation}", operation);
            }
        }

        private Task PublishConnectionStatusAsync(ConnectionStatus status)
        {
            return ConnectionStatusChanged?.Invoke(status) ?? Task.CompletedTask;
        }

        private Task PublishReadinessAsync(bool ready)
        {
            return ReadinessChanged?.Invoke(ready) ?? Task.CompletedTask;
        }

        private Task InvokeMarketBarReceivedAsync(MarketBar bar)
        {
            return MarketBarReceived?.Invoke(bar) ?? Task.CompletedTask;
        }

        private Task InvokeMarketDataEventReceivedAsync(CanonicalMarketDataEvent marketEvent)
        {
            return MarketDataEventReceived?.Invoke(marketEvent) ?? Task.CompletedTask;
        }

        private Task InvokeAccountUpdatedAsync(AccountInfo account)
        {
            return AccountUpdated?.Invoke(account) ?? Task.CompletedTask;
        }

        private Task InvokePositionUpdatedAsync(PositionDto position)
        {
            return PositionUpdated?.Invoke(position) ?? Task.CompletedTask;
        }

        private Task InvokeOrderStatusUpdatedAsync(OrderStatusDto status)
        {
            return OrderStatusUpdated?.Invoke(status) ?? Task.CompletedTask;
        }

        private Task InvokeOrderFilledAsync(OrderStatusDto status)
        {
            return OrderFilled?.Invoke(status) ?? Task.CompletedTask;
        }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
        }

        private sealed class AccountSummaryRequest
        {
            private readonly string _requestedAccountId;
            private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, decimal>> _values = new(StringComparer.OrdinalIgnoreCase);

            public AccountSummaryRequest(string requestedAccountId)
            {
                _requestedAccountId = requestedAccountId;
            }

            public TaskCompletionSource<AccountInfo> Completion { get; } = NewTcs<AccountInfo>();

            public void Record(string account, string tag, string value)
            {
                if (!decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue)) return;
                var accountValues = _values.GetOrAdd(account, _ => new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase));
                accountValues[tag] = decimalValue;
            }

            public AccountInfo Build()
            {
                var selected = _values.FirstOrDefault(pair => string.IsNullOrWhiteSpace(_requestedAccountId)
                    || pair.Key.Equals(_requestedAccountId, StringComparison.OrdinalIgnoreCase));

                if (selected.Key == null)
                {
                    selected = _values.FirstOrDefault();
                }

                var accountId = selected.Key ?? _requestedAccountId;
                var values = selected.Value ?? new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

                return new AccountInfo
                {
                    AccountId = accountId ?? string.Empty,
                    NetLiquidation = values.TryGetValue("NetLiquidation", out var netLiquidation) ? netLiquidation : 0m,
                    AvailableFunds = values.TryGetValue("AvailableFunds", out var availableFunds) ? availableFunds : 0m,
                    BuyingPower = values.TryGetValue("BuyingPower", out var buyingPower) ? buyingPower : 0m,
                    MarginRequirement = values.TryGetValue("MaintMarginReq", out var marginRequirement) ? marginRequirement : 0m
                };
            }
        }

        private sealed class PositionsRequest
        {
            private readonly string _requestedAccountId;
            private readonly ConcurrentBag<PositionDto> _positions = new();

            public PositionsRequest(string requestedAccountId)
            {
                _requestedAccountId = requestedAccountId;
            }

            public TaskCompletionSource<IEnumerable<PositionDto>> Completion { get; } = NewTcs<IEnumerable<PositionDto>>();

            public void Record(PositionDto position)
            {
                if (!string.IsNullOrWhiteSpace(_requestedAccountId)
                    && !position.AccountId.Equals(_requestedAccountId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _positions.Add(position);
            }

            public IEnumerable<PositionDto> Build() => _positions.ToArray();
        }

        private sealed class OpenOrdersRequest
        {
            private readonly ConcurrentBag<OrderStatusDto> _orders = new();

            public TaskCompletionSource<IEnumerable<OrderStatusDto>> Completion { get; } = NewTcs<IEnumerable<OrderStatusDto>>();

            public void Record(OrderStatusDto order)
            {
                _orders.Add(order);
            }

            public IEnumerable<OrderStatusDto> Build() => _orders.ToArray();
        }

        private sealed class HistoricalBarsRequest
        {
            public HistoricalBarsRequest(string symbol, string timeframe)
            {
                Symbol = symbol;
                Timeframe = timeframe;
            }

            public string Symbol { get; }
            public string Timeframe { get; }
            public ConcurrentBag<MarketBar> Bars { get; } = new();
            public TaskCompletionSource<IEnumerable<MarketBar>> Completion { get; } = NewTcs<IEnumerable<MarketBar>>();

            public void Record(MarketBar bar)
            {
                Bars.Add(bar);
            }
        }

        private sealed class StreamingSubscription : IMarketDataSubscription
        {
            private readonly Action _dispose;
            private readonly StreamingBarCompletionBuffer _completionBuffer = new();

            public StreamingSubscription(int requestId, Channel<MarketBar> channel, Action dispose)
            {
                RequestId = requestId;
                Channel = channel;
                _dispose = dispose;
            }

            public int RequestId { get; }
            public string Symbol { get; private set; } = string.Empty;
            public string Timeframe { get; private set; } = string.Empty;
            public bool BackfillCompleted { get; private set; }
            public int BackfillBarsIgnored { get; private set; }
            public Channel<MarketBar> Channel { get; }
            public ChannelReader<MarketBar> Reader => Channel.Reader;

            public void SetContext(string symbol, string timeframe)
            {
                Symbol = symbol;
                Timeframe = timeframe;
            }

            public void RecordBackfillBar()
            {
                BackfillBarsIgnored++;
            }

            public void MarkBackfillCompleted()
            {
                BackfillCompleted = true;
            }

            public MarketBar? AcceptLiveUpdate(MarketBar update)
            {
                return _completionBuffer.AcceptUpdate(update);
            }

            public ValueTask DisposeAsync()
            {
                _dispose();
                Channel.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }

        private sealed class LevelOneSubscription
        {
            private long _bidSequence;
            private long _askSequence;
            private long _tradeSequence;

            public LevelOneSubscription(string symbol)
            {
                Symbol = symbol;
            }

            public string Symbol { get; }
            public long NextSequence(MarketDataEventKind kind) => kind switch
            {
                MarketDataEventKind.Bid => Interlocked.Increment(ref _bidSequence),
                MarketDataEventKind.Ask => Interlocked.Increment(ref _askSequence),
                MarketDataEventKind.Trade => Interlocked.Increment(ref _tradeSequence),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
        }
    }
}
#endif
