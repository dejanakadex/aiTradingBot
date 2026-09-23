using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Background
{
    public sealed class HistoricalBackfillHostedService : BackgroundService
    {
        private readonly IHistoricalBackfillService _backfill;
        private readonly IIbkrConnectionService _connection;
        private readonly HistoricalBackfillSettings _settings;
        private readonly ILogger<HistoricalBackfillHostedService> _logger;

        public HistoricalBackfillHostedService(
            IHistoricalBackfillService backfill,
            IIbkrConnectionService connection,
            IOptions<HistoricalBackfillSettings> settings,
            ILogger<HistoricalBackfillHostedService> logger)
        {
            _backfill = backfill;
            _connection = connection;
            _settings = settings.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("Historical backfill worker is disabled by configuration.");
                return;
            }

            _logger.LogInformation("Historical backfill worker started.");
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await _backfill.SynchronizePlanAsync(stoppingToken).ConfigureAwait(false);
                    if (_connection.Status == ConnectionStatus.Connected)
                    {
                        await _backfill.RunNextSegmentAsync(stoppingToken).ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromSeconds(_settings.WorkerIntervalSeconds), stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Historical backfill worker cancellation requested.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Historical backfill worker stopped after an unexpected failure.");
            }
        }
    }
}
