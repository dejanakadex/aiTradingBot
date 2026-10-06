using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class FacioQuoIndicatorAdapterTests
{
    [Fact]
    public void ComparisonMakesWarmupAndFormulaDifferencesExplicit()
    {
        var settings = new CanonicalFeatureSettings
        {
            EmaShortPeriod = 3, EmaLongPeriod = 5, RsiPeriod = 3, AtrPeriod = 3,
            VolumeAveragePeriod = 3, MomentumLookbackCandles = 2, MeanReversionLookbackCandles = 3
        };
        var engine = new FeatureEngine(settings);
        var adapter = new FacioQuoIndicatorAdapter();
        var start = new DateTime(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);
        var candles = Enumerable.Range(0, 30).Select(index =>
        {
            var close = 100m + index * 0.15m + (index % 3 == 0 ? 0.7m : -0.4m);
            var range = 0.5m + index * 0.03m;
            return new Candle("SPY", Timeframe.OneMinute, start.AddMinutes(index), close - 0.1m,
                close + range, close - range, close, 100m + index, "SPY-ID");
        }).ToArray();

        for (var count = 1; count <= candles.Length; count++)
        {
            var sample = candles.Take(count).ToArray();
            var runtime = engine.ComputeFeatures(sample);
            var library = adapter.Compute(sample, 3, 5, 3, 3);
            AssertNear(FeatureEngine.ComputeEma(sample, 3), library.EmaShort, $"EMA(3), count={count}");
            AssertNear(FeatureEngine.ComputeEma(sample, 5), library.EmaLong, $"EMA(5), count={count}");
            AssertNear(FeatureEngine.ComputeRsi(sample, 3), library.Rsi, $"RSI(3), count={count}");
            Assert.Equal(library.EmaShort, runtime.EmaShort);
            Assert.Equal(library.EmaLong, runtime.EmaLong);
            Assert.Equal(library.Rsi, runtime.Rsi);
        }

        var last = engine.ComputeFeatures(candles);
        var comparison = adapter.Compute(candles, 3, 5, 3, 3);
        Assert.NotNull(last.Atr);
        Assert.NotNull(comparison.Atr);
        Assert.Equal(FeatureEngine.ComputeAtr(candles, 3), last.Atr);
        Assert.True(Math.Abs(last.Atr.Value - comparison.Atr.Value) > 0.001m,
            "Legacy ATR is a rolling mean of true ranges; FacioQuo ATR uses Wilder smoothing.");
    }

    private static void AssertNear(decimal? expected, decimal? actual, string name)
    {
        Assert.Equal(expected.HasValue, actual.HasValue);
        if (expected.HasValue)
            Assert.True(Math.Abs(expected.Value - actual!.Value) < 0.000001m,
                $"{name}: legacy={expected.Value}, FacioQuo={actual.Value}");
    }
}
