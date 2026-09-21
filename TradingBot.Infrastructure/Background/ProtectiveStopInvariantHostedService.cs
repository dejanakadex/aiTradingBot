using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Background
{
    public sealed class ProtectiveStopInvariantHostedService : BackgroundService
    {
        private readonly IProtectiveStopMonitor _monitor;
        private readonly ITradingEngineStatusService _statusService;
        private readonly TradingSettings _settings;
        private readonly ILogger<ProtectiveStopInvariantHostedService> _logger;

        public ProtectiveStopInvariantHostedService(
            IProtectiveStopMonitor monitor,
            ITradingEngineStatusService statusService,
            IOptions<TradingSettings> settings,
            ILogger<ProtectiveStopInvariantHostedService> logger)
        {
            _monitor = monitor;
            _statusService = statusService;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ProtectiveStopMonitorIntervalSeconds));
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (_statusService.Current.State == TradingEngineState.Ready)
                    {
                        await _monitor.ValidateAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Protective stop invariant background check failed.");
                    _statusService.SetState(TradingEngineState.Degraded, false, "Protective stop invariant background check failed. Trading disabled.", new[] { ex.Message });
                }
            }
        }
    }
}
