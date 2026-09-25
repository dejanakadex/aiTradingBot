using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class CandidateLabelingHostedService : BackgroundService
    {
        private readonly ICandidateResearchService _research;
        private readonly IClock _clock;
        private readonly CandidateLabelingSettings _settings;
        private readonly ILogger<CandidateLabelingHostedService> _logger;

        public CandidateLabelingHostedService(
            ICandidateResearchService research,
            IClock clock,
            IOptions<CandidateLabelingSettings> settings,
            ILogger<CandidateLabelingHostedService> logger)
        {
            _research = research;
            _clock = clock;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("Candidate labeling is disabled by configuration.");
                return;
            }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.WorkerIntervalSeconds));
            do
            {
                try
                {
                    var count = await _research.LabelMatureCandidatesAsync(_clock.UtcNow, cancellationToken: stoppingToken).ConfigureAwait(false);
                    if (count > 0) _logger.LogInformation("Calculated {Count} mature research candidate labels.", count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Candidate labeling cycle failed; pending labels will be retried.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
    }
}
