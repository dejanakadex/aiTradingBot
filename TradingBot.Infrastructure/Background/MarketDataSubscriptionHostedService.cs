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
        private readonly OpenAiSettings _openAiSettings;
        private readonly ILogger<MarketDataSubscriptionHostedService> _logger;
        private readonly List<IMarketDataSubscription> _subscriptions = new();
        private readonly List<Task> _readerTasks = new();

        public MarketDataSubscriptionHostedService(
            IMarketDataService marketDataService,
            MarketDataPipeline marketDataPipeline,
            ITradingEngineStatusService statusService,
            IIbkrConnectionService connectionService,
            IOptions<TradingSettings> tradingSettings,
            IOptions<OpenAiSettings> openAiSettings,
            ILogger<MarketDataSubscriptionHostedService> logger)
        {
            _marketDataService = marketDataService ?? throw new ArgumentNullException(nameof(marketDataService));
            _marketDataPipeline = marketDataPipeline ?? throw new ArgumentNullException(nameof(marketDataPipeline));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _tradingSettings = tradingSettings.Value;
            _openAiSettings = openAiSettings.Value;
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

            foreach (var symbol in GetConfiguredSymbols())
            {
                foreach (var timeframe in GetConfiguredTimeframes())
                {
                    if (_connectionService.Status != ConnectionStatus.Connected)
                    {
                        _logger.LogWarning(
                            "Stopped starting market data subscriptions because IBKR changed to {Status}.",
                            _connectionService.Status);
                        return;
                    }

                    await SeedHistoricalCandlesAsync(symbol, timeframe, cancellationToken).ConfigureAwait(false);
                    await SubscribeAsync(symbol, timeframe, cancellationToken).ConfigureAwait(false);
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

        private async Task SeedHistoricalCandlesAsync(string symbol, string timeframe, CancellationToken cancellationToken)
        {
            try
            {
                var count = GetHistoricalSeedCount(timeframe);
                var bars = await _marketDataService.GetHistoricalBarsAsync(symbol, timeframe, count, cancellationToken).ConfigureAwait(false);
                var seeded = 0;
                foreach (var bar in bars.OrderBy(b => b.TimestampUtc))
                {
                    await _marketDataPipeline.ProcessMarketBarAsync(
                        bar,
                        cancellationToken,
                        allowStaleSeedCandle: true,
                        publishToEventBus: false).ConfigureAwait(false);
                    seeded++;
                }

                _logger.LogInformation("Seeded {Count} historical candles for {Symbol} {Timeframe}.", seeded, symbol, timeframe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed historical candles for {Symbol} {Timeframe}.", symbol, timeframe);
            }
        }

        private async Task SubscribeAsync(string symbol, string timeframe, CancellationToken cancellationToken)
        {
            try
            {
                var subscription = await _marketDataService.SubscribeAsync(symbol, timeframe, cancellationToken).ConfigureAwait(false);
                _subscriptions.Add(subscription);
                _readerTasks.Add(ReadSubscriptionAsync(symbol, timeframe, subscription, cancellationToken));
                _logger.LogInformation("Subscribed to market data for {Symbol} {Timeframe}.", symbol, timeframe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to market data for {Symbol} {Timeframe}.", symbol, timeframe);
            }
        }

        private async Task ReadSubscriptionAsync(
            string symbol,
            string timeframe,
            IMarketDataSubscription subscription,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var bar in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await _marketDataPipeline.ProcessMarketBarAsync(bar, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogWarning("Market data subscription completed for {Symbol} {Timeframe}.", symbol, timeframe);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Market data subscription reader canceled for {Symbol} {Timeframe}.", symbol, timeframe);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Market data subscription reader failed for {Symbol} {Timeframe}.", symbol, timeframe);
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

        private IReadOnlyList<string> GetConfiguredSymbols()
        {
            var symbols = (_tradingSettings.Symbols ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return symbols.Length == 0 ? new[] { "SPY" } : symbols;
        }

        private IReadOnlyList<string> GetConfiguredTimeframes()
        {
            var timeframes = (_tradingSettings.MarketDataTimeframes ?? Array.Empty<string>())
                .Select(NormalizeTimeframe)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return timeframes.Length == 0 ? new[] { "1m", "5m", "15m" } : timeframes;
        }

        private int GetHistoricalSeedCount(string timeframe)
        {
            return NormalizeTimeframe(timeframe) switch
            {
                "1m" => Math.Max(1, _openAiSettings.MarketContext.OneMinuteCandles),
                "5m" => Math.Max(1, _openAiSettings.MarketContext.FiveMinuteCandles),
                "15m" => Math.Max(1, _openAiSettings.MarketContext.FifteenMinuteCandles),
                _ => 50
            };
        }

        private static string NormalizeTimeframe(string timeframe)
        {
            return timeframe.Trim().ToLowerInvariant() switch
            {
                "1m" or "1 min" or "1 minute" => "1m",
                "5m" or "5 mins" or "5 minutes" => "5m",
                "15m" or "15 mins" or "15 minutes" => "15m",
                _ => string.Empty
            };
        }
    }
}
