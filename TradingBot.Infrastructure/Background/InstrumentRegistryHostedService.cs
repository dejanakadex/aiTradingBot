using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class InstrumentRegistryHostedService : IHostedService
    {
        private readonly IInstrumentRegistryService _registry;
        private readonly ILogger<InstrumentRegistryHostedService> _logger;

        public InstrumentRegistryHostedService(
            IInstrumentRegistryService registry,
            ILogger<InstrumentRegistryHostedService> logger)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var result = await _registry.SynchronizeConfiguredAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Instrument registry synchronized: added={Added}, updated={Updated}, disabled={Disabled}, unchanged={Unchanged}, total={Total}.",
                result.Added,
                result.Updated,
                result.Disabled,
                result.Unchanged,
                result.Instruments.Count);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
