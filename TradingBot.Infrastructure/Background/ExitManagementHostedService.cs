using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Background
{
    public sealed class ExitManagementHostedService : IHostedService
    {
        private readonly IExitManagementService _exitManagementService;
        private readonly ITradingEngineStatusService _statusService;
        private readonly ILogger<ExitManagementHostedService> _logger;

        public ExitManagementHostedService(
            IExitManagementService exitManagementService,
            ITradingEngineStatusService statusService,
            ILogger<ExitManagementHostedService> logger)
        {
            _exitManagementService = exitManagementService;
            _statusService = statusService;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _exitManagementService.RestoreAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Exit management service initialized.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Exit management startup restore failed. Trading must fail closed.");
                _statusService.SetState(TradingEngineState.Degraded, false, "Exit management startup restore failed.", new[] { ex.Message });
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Exit management service stopping.");
            return Task.CompletedTask;
        }
    }
}
