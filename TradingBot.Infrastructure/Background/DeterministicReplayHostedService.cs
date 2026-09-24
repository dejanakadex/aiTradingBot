using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class DeterministicReplayHostedService : BackgroundService
    {
        private readonly IDeterministicReplayService _replayService;
        private readonly ReplaySettings _settings;
        private readonly ILogger<DeterministicReplayHostedService> _logger;

        public DeterministicReplayHostedService(
            IDeterministicReplayService replayService,
            IOptions<ReplaySettings> settings,
            ILogger<DeterministicReplayHostedService> logger)
        {
            _replayService = replayService ?? throw new ArgumentNullException(nameof(replayService));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("Deterministic replay worker is disabled by configuration.");
                return;
            }

            _logger.LogInformation("Deterministic replay worker started.");
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var runnable = await _replayService.GetRunnableRunIdsAsync(stoppingToken).ConfigureAwait(false);
                    if (runnable.Count == 0)
                    {
                        await Task.Delay(_settings.WorkerPollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var replayRunId in runnable)
                    {
                        await _replayService.ProcessBatchAsync(replayRunId, _settings.EventsPerBatch, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Deterministic replay worker iteration failed.");
                    await Task.Delay(_settings.WorkerPollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
                }
            }
            _logger.LogInformation("Deterministic replay worker stopped.");
        }
    }
}
