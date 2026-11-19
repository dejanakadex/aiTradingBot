using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure.Services;

/// <summary>Shared collection/replay bound. Does not rewrite timestamps or relax stale/execution checks.</summary>
internal static class ForexQuoteTimePolicy
{
    public static bool IsTooFarAhead(DateTime eventUtc, DateTime receivedUtc, ShortAggregateSettings settings)
    {
        if (settings.ForexFutureToleranceMilliseconds is < 0 or > 500)
            throw new ArgumentOutOfRangeException(nameof(settings.ForexFutureToleranceMilliseconds));
        return eventUtc - receivedUtc > TimeSpan.FromMilliseconds(settings.ForexFutureToleranceMilliseconds);
    }

    // Simulation cannot act before either recorded time, even when small source skew is tolerated.
    public static DateTime AvailableAt(DateTime eventUtc, DateTime receivedUtc) =>
        eventUtc > receivedUtc ? eventUtc : receivedUtc;
}
