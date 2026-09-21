using Microsoft.Extensions.Diagnostics.HealthChecks;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Web.HealthChecks
{
    public sealed class TradingEngineReadinessHealthCheck : IHealthCheck
    {
        private readonly ITradingEngineStatusService _statusService;

        public TradingEngineReadinessHealthCheck(ITradingEngineStatusService statusService)
        {
            _statusService = statusService;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var current = _statusService.Current;
            if (current.State == TradingEngineState.Ready && current.TradingEnabled)
            {
                return Task.FromResult(HealthCheckResult.Healthy("Trading engine is ready."));
            }

            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Trading engine is not ready. State={current.State}; TradingEnabled={current.TradingEnabled}; Message={current.Message}."));
        }
    }
}
