using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Background
{
    public sealed class BrokerStateReconciliationHostedService : IHostedService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ITradingEngineStatusService _statusService;
        private readonly ILogger<BrokerStateReconciliationHostedService> _logger;

        public BrokerStateReconciliationHostedService(
            IServiceProvider serviceProvider,
            ITradingEngineStatusService statusService,
            ILogger<BrokerStateReconciliationHostedService> logger)
        {
            _serviceProvider = serviceProvider;
            _statusService = statusService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _statusService.SetState(TradingEngineState.Starting, false, "Application started. Trading remains disabled until reconciliation completes.");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var reconciliationService = scope.ServiceProvider.GetService<IBrokerStateReconciliationService>();
                if (reconciliationService == null)
                {
                    _statusService.SetState(TradingEngineState.Degraded, false, "Broker reconciliation service is not available. Trading remains disabled.");
                    return;
                }

                await reconciliationService.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Broker state reconciliation failed");
                _statusService.SetState(TradingEngineState.Faulted, false, "Broker reconciliation failed. Trading remains disabled.", new[] { ex.Message });
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _statusService.SetState(TradingEngineState.Paused, false, "Application stopping. Trading disabled.");
            return Task.CompletedTask;
        }
    }
}
