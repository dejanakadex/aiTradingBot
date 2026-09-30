using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class InstrumentRolloutHostedService : BackgroundService
    {
        private readonly IInstrumentRolloutService _rollout;
        private readonly InstrumentRolloutSettings _settings;
        private readonly ILogger<InstrumentRolloutHostedService> _logger;

        public InstrumentRolloutHostedService(IInstrumentRolloutService rollout, IOptions<InstrumentRolloutSettings> settings, ILogger<InstrumentRolloutHostedService> logger)
        {
            _rollout = rollout;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled) return;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.EvaluationIntervalSeconds));
            do
            {
                try
                {
                    var results = await _rollout.EvaluateAllAsync(true, stoppingToken).ConfigureAwait(false);
                    foreach (var result in results.Where(x => x.StatusBefore != x.StatusAfter || x.Suspended))
                        _logger.LogWarning("Instrument rollout changed {InstrumentId}: {Before} -> {After}; suspended={Suspended}; reasons={Reasons}",
                            result.InstrumentId, result.StatusBefore, result.StatusAfter, result.Suspended, string.Join(" ", result.Reasons));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Instrument rollout evaluation failed; no readiness is granted optimistically.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }
}
