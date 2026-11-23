using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Background;

public sealed class CompletedTradeAnalysisHostedService(CompletedTradeAnalysisService analysis,
    ILogger<CompletedTradeAnalysisHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cursor = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { cursor = await analysis.SynchronizeBatchAsync(cursor, 100, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Completed-trade analysis failed; durable source will be retried."); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
