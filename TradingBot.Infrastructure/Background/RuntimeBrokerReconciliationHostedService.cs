using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Background
{
    public sealed class RuntimeBrokerReconciliationHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ITradingEngineStatusService _statusService;
        private readonly ILogger<RuntimeBrokerReconciliationHostedService> _logger;
        private readonly SemaphoreSlim _reconciliationLock = new(1, 1);
        private IIbkrConnectionService? _connectionService;

        public RuntimeBrokerReconciliationHostedService(
            IServiceProvider serviceProvider,
            ITradingEngineStatusService statusService,
            ILogger<RuntimeBrokerReconciliationHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _statusService = statusService;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _connectionService = _serviceProvider.GetService<IIbkrConnectionService>();
            if (_connectionService == null)
            {
                _logger.LogInformation("Runtime broker reconciliation trigger disabled because no IBKR connection service is registered.");
                return Task.CompletedTask;
            }

            _connectionService.ConnectionStatusChanged += OnConnectionStatusChangedAsync;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_connectionService != null)
            {
                _connectionService.ConnectionStatusChanged -= OnConnectionStatusChangedAsync;
            }

            _reconciliationLock.Dispose();
            return Task.CompletedTask;
        }

        private async Task OnConnectionStatusChangedAsync(ConnectionStatus status)
        {
            if (status != ConnectionStatus.Connected)
            {
                if (_statusService.Current.State == TradingEngineState.Ready)
                {
                    _statusService.SetState(TradingEngineState.Degraded, false, $"IBKR connection changed to {status}. Runtime reconciliation is required.");
                }

                return;
            }

            var currentState = _statusService.Current.State;
            if (currentState is TradingEngineState.Starting or TradingEngineState.Connecting or TradingEngineState.Reconciling)
            {
                _logger.LogInformation("IBKR connected event occurred during {State}; startup reconciliation owns this pass.", currentState);
                return;
            }

            if (!await _reconciliationLock.WaitAsync(0).ConfigureAwait(false))
            {
                _logger.LogInformation("Runtime reconciliation is already running; duplicate trigger ignored.");
                return;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var reconciliationService = scope.ServiceProvider.GetRequiredService<IBrokerStateReconciliationService>();
                _logger.LogInformation("IBKR connected event received. Running runtime broker reconciliation.");
                await reconciliationService.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Runtime broker reconciliation failed.");
                _statusService.SetState(TradingEngineState.Faulted, false, "Runtime broker reconciliation failed. Trading disabled.", new[] { ex.Message });
            }
            finally
            {
                _reconciliationLock.Release();
            }
        }
    }
}
