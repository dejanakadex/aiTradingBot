using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

public sealed partial class ExitManagementService
{
    // Runs from the existing independent exit timer, not from stock minute/volume candles.
    private async Task ProcessForexTrailingAsync(DateTime now, CancellationToken ct)
    {
        if (_settings.Mode != ExitStrategyMode.AtrTrailing) return;
        var features = _serviceProvider.GetService<ForexFeatureService>();
        var settings = _serviceProvider.GetService<IOptions<ForexExecutionSettings>>()?.Value;
        if (features == null || settings == null) return;
        foreach (var instrument in _tradingSettings.GetConfiguredInstruments().Where(x => x.InstrumentType == InstrumentType.Forex))
        {
            var profile = settings.Profiles.SingleOrDefault(x => x.InstrumentId == instrument.InstrumentId);
            if (profile == null) continue;
            var input = features.GetInput(instrument.InstrumentId, profile.TrailingIntervalSeconds, now);
            if (input?.Bid is not > 0 || input.Ask is not > 0 || input.BidTimeUtc == null
                || input.BidTimeUtc > now || now - input.BidTimeUtc > TimeSpan.FromSeconds(5)) continue;
            var frame = _featureEngine.ComputeFeatures(input);
            if (frame.PriceFeaturesReady != true || frame.Atr is not > 0) continue;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ids = await db.ExitManagementRecords.Where(x => x.InstrumentId == instrument.InstrumentId
                && x.FilledQuantity > x.ExitedQuantity && x.ProtectedQuantity > 0 && x.State != ExitManagementState.Faulted
                && x.State != ExitManagementState.ExitSubmitted && x.State != ExitManagementState.ExitCancelPending
                && x.State != ExitManagementState.EntryCancelPending && x.State != ExitManagementState.TimeExitDeferred
                && (!x.ExitDeadlineUtc.HasValue || x.ExitDeadlineUtc > now)).Select(x => x.Id).ToArrayAsync(ct);
            foreach (var id in ids)
            {
                var gate = await EnterLockAsync($"exit:{id}", ct);
                try
                {
                    await using var scoped = await _dbFactory.CreateDbContextAsync(ct);
                    var record = await scoped.ExitManagementRecords.SingleAsync(x => x.Id == id, ct);
                    // HighestPriceSinceEntry is updated from observed executable bids, not midpoint highs.
                    var bid = input.Bid.Value;
                    var candle = new Candle(instrument.Symbol, input.Candles[^1].Timeframe, input.Candles[^1].TimestampUtc,
                        bid, bid, bid, bid, null, instrument.InstrumentId);
                    await ProcessMarketCandleForRecordAsync(scoped, record, candle, ct, frame.Atr);
                    await scoped.SaveChangesAsync(ct);
                }
                finally { gate.Release(); }
            }
        }
    }
}
