using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Web.HealthChecks
{
    public sealed class IbkrConnectionHealthCheck : IHealthCheck
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IbkrSettings _ibkrSettings;
        private readonly TradingSettings _tradingSettings;

        public IbkrConnectionHealthCheck(
            IServiceProvider serviceProvider,
            IOptions<IbkrSettings> ibkrSettings,
            IOptions<TradingSettings> tradingSettings)
        {
            _serviceProvider = serviceProvider;
            _ibkrSettings = ibkrSettings.Value;
            _tradingSettings = tradingSettings.Value;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var connection = _serviceProvider.GetService<IIbkrConnectionService>();
            if (connection is null)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("IBKR connection service is not registered."));
            }

            if (connection is UnavailableIbkrConnectionService)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("IBKR adapter is not registered. Trading remains disabled until a concrete IBKR integration is configured."));
            }

            var endpoint = $"{_ibkrSettings.Host}:{_ibkrSettings.GetPort(_tradingSettings.OperatingMode)}";
            return Task.FromResult(connection.Status == ConnectionStatus.Connected
                ? HealthCheckResult.Healthy("IBKR is connected.")
                : HealthCheckResult.Unhealthy($"IBKR is not connected. Status={connection.Status}. Endpoint={endpoint}. Check that TWS/Gateway is running and API socket access is enabled on that port."));
        }
    }
}
