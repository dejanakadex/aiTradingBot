using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Background;

/// <summary>Runs independently of tick ingestion; no trading-enabled requirement and no broker/order calls.</summary>
public sealed class ForexSignalHostedService(ForexSignalTriggerService signals, ILatestMarketDataService latest,
    IOptions<ForexSignalSettings> settings, IIbkrConnectionService connection, IClock clock, ILogger<ForexSignalHostedService> logger) : BackgroundService
{
    public async Task RunOnceAsync(DateTime nowUtc, CancellationToken token = default)
    {
        if (connection.Status != ConnectionStatus.Connected) return;
        latest.AdvanceTo(nowUtc);
        foreach (var instrumentId in settings.Value.Profiles.Where(x => x.Selection.Enabled).Select(x => x.Selection.InstrumentId).Distinct())
            await signals.ProcessAsync(instrumentId, nowUtc, token).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.Profiles.Any(x => x.Selection.Enabled)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await RunOnceAsync(clock.UtcNow, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "FX shadow signal cycle failed; collection remains independent."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
