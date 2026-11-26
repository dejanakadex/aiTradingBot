using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public class IbkrConnectionService : IIbkrConnectionService, IDisposable
    {
        private readonly IIbkrAdapter _adapter;
        private readonly IbkrOptions _options;
        private readonly ILogger<IbkrConnectionService> _logger;

        private CancellationTokenSource? _cts;
        private Task? _reconnectTask;
        private int _restartSocketRequested;
        private readonly object _sync = new();

        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;

        public IbkrConnectionService(IIbkrAdapter adapter, IbkrOptions? options, ILogger<IbkrConnectionService> logger)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _options = options ?? new IbkrOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _adapter.ConnectionStatusChanged += Adapter_ConnectionStatusChanged;
            _adapter.ReadinessChanged += Adapter_ReadinessChanged;
        }

        private Task Adapter_ReadinessChanged(bool ready)
        {
            _logger.LogInformation("IBKR adapter readiness changed: {Ready}", ready);
            return Task.CompletedTask;
        }

        private Task Adapter_ConnectionStatusChanged(ConnectionStatus status)
        {
            _logger.LogInformation("IBKR adapter status: {Status}", status);
            Status = status;
            if (status == ConnectionStatus.Failed) Interlocked.Exchange(ref _restartSocketRequested, 1);
            if (status is ConnectionStatus.Disconnected or ConnectionStatus.Failed)
            {
                StartReconnectLoopIfNeeded(status);
            }

            return ConnectionStatusChanged?.Invoke(status) ?? Task.CompletedTask;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_cts != null)
                {
                    _logger.LogInformation(
                        "IBKR connect request ignored because connection is already active or in progress. Status={Status}, AdapterConnected={AdapterConnected}",
                        Status,
                        _adapter.IsConnected);
                    return;
                }

                Status = ConnectionStatus.Connecting;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            }

            try
            {
                _logger.LogInformation("Starting IBKR adapter connection to {Host}:{Port} with clientId={ClientId}", _options.Host, _options.Port, _options.ClientId);
                await _adapter.StartAsync(_cts.Token).ConfigureAwait(false);

                // if not connected immediately, start background reconnect monitor
                if (!_adapter.IsConnected)
                {
                    StartReconnectLoopIfNeeded(ConnectionStatus.Disconnected);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connect canceled");
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                if (_cts == null) return;
                _cts.Cancel();
            }

            try
            {
                if (_reconnectTask != null) await _reconnectTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("IBKR reconnect loop canceled during disconnect.");
            }
            finally
            {
                try { await _adapter.StopAsync(cancellationToken).ConfigureAwait(false); } catch (Exception ex) { _logger.LogWarning(ex, "Error stopping adapter"); }
                lock (_sync)
                {
                    _cts?.Dispose();
                    _cts = null;
                    _reconnectTask = null;
                }
            }
        }

        private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
        {
            var delay = _options.ReconnectDelayMs;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // A failed SDK reader is different from a recoverable TWS upstream interruption.
                    if (Interlocked.Exchange(ref _restartSocketRequested, 0) != 0)
                        await _adapter.StopAsync(cancellationToken).ConfigureAwait(false);
                    // A live API socket does not prove TWS connectivity to IBKR (error 1100).
                    // Keep the reader alive for 1101/1102; reopening the same socket cannot repair that outage.
                    if (!_adapter.IsConnected)
                    {
                        _logger.LogInformation("Attempting IBKR adapter start (reconnect)");
                        await _adapter.StartAsync(cancellationToken).ConfigureAwait(false);
                    }
                    if (_adapter.IsConnected && Status == ConnectionStatus.Connected)
                    {
                        _logger.LogInformation("IBKR adapter reconnected");
                        return;
                    }
                    if (_adapter.IsConnected)
                        _logger.LogWarning("IBKR API socket is open, but broker recovery is not confirmed. Status={Status}; waiting for the adapter recovery event.", Status);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Reconnect canceled");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect attempt failed");
                }

                var ms = Math.Min(delay, _options.MaximumReconnectDelayMs);
                _logger.LogInformation("Waiting {Delay}ms before next reconnect attempt", ms);
                try
                {
                    await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("IBKR reconnect delay canceled.");
                    throw;
                }
                delay = Math.Min(delay * 2, _options.MaximumReconnectDelayMs);
            }
        }

        private void StartReconnectLoopIfNeeded(ConnectionStatus triggerStatus)
        {
            CancellationToken reconnectToken;
            lock (_sync)
            {
                if (_cts == null || _cts.IsCancellationRequested)
                {
                    return;
                }

                if (_reconnectTask is { IsCompleted: false })
                {
                    return;
                }

                Status = ConnectionStatus.Reconnecting;
                reconnectToken = _cts.Token;
                _reconnectTask = Task.Run(() => ReconnectLoopAsync(reconnectToken), CancellationToken.None);
            }

            _logger.LogWarning(
                "IBKR connection changed to {Status}; reconnect loop started.",
                triggerStatus);
        }

        public void Dispose()
        {
            try
            {
                DisconnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "IBKR connection service dispose encountered an error after shutdown was requested.");
            }
        }
    }
}
