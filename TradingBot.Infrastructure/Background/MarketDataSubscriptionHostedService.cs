using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Background
{
    public sealed class MarketDataSubscriptionHostedService : BackgroundService
    {
        private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

        private readonly IMarketDataService _marketDataService;
        private readonly MarketDataPipeline _marketDataPipeline;
        private readonly ITradingEngineStatusService _statusService;
        private readonly IIbkrConnectionService _connectionService;
        private readonly TradingSettings _tradingSettings;
        private readonly ILogger<MarketDataSubscriptionHostedService> _logger;
        private readonly List<IMarketDataSubscription> _subscriptions = new();
        private readonly List<Task> _readerTasks = new();

        public MarketDataSubscriptionHostedService(
            IMarketDataService marketDataService,
            MarketDataPipeline marketDataPipeline,
            ITradingEngineStatusService statusService,
            IIbkrConnectionService connectionService,
            IOptions<TradingSettings> tradingSettings,
            ILogger<MarketDataSubscriptionHostedService> logger)
        {
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
            _marketDataPipeline = marketDataPipeline ?? throw new ArgumentNullException(nameof(marketDataPipeline));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _tradingSettings = tradingSettings.Value;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Market data subscription service started.");
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
                    await StartSubscriptionsAsync(stoppingToken).ConfigureAwait(false);

                    if (_readerTasks.Count == 0)
                    {
                        var reason = _connectionService.Status == ConnectionStatus.Connected
                            ? "Market data subscriptions could not be started."
                            : $"Market data subscriptions could not be started because IBKR is {_connectionService.Status}.";

                        _statusService.SetState(TradingEngineState.Degraded, false, reason);
                        await Task.Delay(RetryInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    await WaitForReadersOrDisconnectAsync(stoppingToken).ConfigureAwait(false);
                    await DisposeSubscriptionsAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Market data subscription service cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Critical failure in market data subscription service.");
                _statusService.SetState(TradingEngineState.Degraded, false, "Market data subscription service failed.", new[] { ex.Message });
            }
            finally
            {
                await DisposeSubscriptionsAsync().ConfigureAwait(false);
                _logger.LogInformation("Market data subscription service stopped.");
            }
        }

        private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = _statusService.Current;
                if (status.State == TradingEngineState.Ready
                    && status.TradingEnabled
                    && status.ReconciliationCompleted
                    && _tradingSettings.Enabled
                    && _connectionService.Status == ConnectionStatus.Connected)
                {
                    return;
                }

                await Task.Delay(ReadyPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task StartSubscriptionsAsync(CancellationToken cancellationToken)
        {
            if (_connectionService.Status != ConnectionStatus.Connected)
            {
                _logger.LogWarning(
                    "Market data subscriptions skipped because IBKR is {Status}.",
                    _connectionService.Status);
                return;
            }

            foreach (var instrument in _tradingSettings.GetEnabledInstruments())
            {
                foreach (var timeframe in instrument.MarketDataTimeframes)
                {
                    if (_connectionService.Status != ConnectionStatus.Connected)
                    {
                        _logger.LogWarning(
                            "Stopped starting market data subscriptions because IBKR changed to {Status}.",
                            _connectionService.Status);
                        return;
                    }

                    await SubscribeAsync(instrument, timeframe, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task WaitForReadersOrDisconnectAsync(CancellationToken cancellationToken)
        {
            var readers = Task.WhenAll(_readerTasks);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_connectionService.Status != ConnectionStatus.Connected)
                {
                    _statusService.SetState(
                        TradingEngineState.Degraded,
                        false,
                        $"IBKR changed to {_connectionService.Status}. Market data subscriptions disposed; reconciliation required.");
                    return;
                }

                var completed = await Task.WhenAny(readers, Task.Delay(ReadyPollInterval, cancellationToken)).ConfigureAwait(false);
                if (completed == readers)
                {
                    await readers.ConfigureAwait(false);
                    _statusService.SetState(
                        TradingEngineState.Degraded,
                        false,
                        "Market data subscriptions ended. Reconciliation required before retry.");
                    return;
                }
            }
        }

        private async Task SubscribeAsync(ConfiguredInstrument instrument, string timeframe, CancellationToken cancellationToken)
        {
            try
            {
                var subscription = await _marketDataService.SubscribeAsync(instrument.Symbol, timeframe, cancellationToken).ConfigureAwait(false);
                _subscriptions.Add(subscription);
                _readerTasks.Add(ReadSubscriptionAsync(instrument, timeframe, subscription, cancellationToken));
                _logger.LogInformation("Subscribed to market data for {InstrumentId} {Symbol} {Timeframe}.", instrument.InstrumentId, instrument.Symbol, timeframe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to market data for {InstrumentId} {Symbol} {Timeframe}.", instrument.InstrumentId, instrument.Symbol, timeframe);
            }
        }

        private async Task ReadSubscriptionAsync(
            ConfiguredInstrument instrument,
            string timeframe,
            IMarketDataSubscription subscription,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var bar in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _marketDataPipeline.ProcessMarketBarAsync(
                        bar,
                        cancellationToken,
                        instrumentId: instrument.InstrumentId,
                        source: "IBKR.StreamingBar").ConfigureAwait(false);
                }

                _logger.LogWarning("Market data subscription completed for {InstrumentId} {Symbol} {Timeframe}.", instrument.InstrumentId, instrument.Symbol, timeframe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Market data subscription reader canceled for {InstrumentId} {Symbol} {Timeframe}.", instrument.InstrumentId, instrument.Symbol, timeframe);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market data subscription reader failed for {InstrumentId} {Symbol} {Timeframe}.", instrument.InstrumentId, instrument.Symbol, timeframe);
            }
        }

        private async Task DisposeSubscriptionsAsync()
        {
            foreach (var subscription in _subscriptions)
            {
                try
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to dispose market data subscription.");
                }
            }

            _subscriptions.Clear();
            _readerTasks.Clear();
        }

    }
}
