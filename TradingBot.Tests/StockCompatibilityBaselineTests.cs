using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

// Synthetic SPY fixtures, frozen before FX support. They are not broker or historical observations.
public sealed class StockCompatibilityBaselineTests
{
    private static readonly DateTime End = new(2026, 10, 23, 14, 30, 0, DateTimeKind.Utc);
    private const string Instrument = "US-STK-SPY-SMART";

    [Theory]
    // Fixed observations from d4bf83c; do not regenerate expectations to make an FX change pass.
    // Columns: final, geometry, context, confirmation, volume, optional location.
    [InlineData(PatternType.Hammer, "0.577142857143", "0.866666666667", "0", "0.914285714286", "0.5", "0")]
    [InlineData(PatternType.BullishEngulfing, "0.566666666667", "0.666666666667", "0", "1", "0.5", "0")]
    [InlineData(PatternType.DoubleBottom, "0.766666666667", "1", "1", "0", "0.666666666667", "1")]
    [InlineData(PatternType.BreakoutAndRetest, "0.715539832285", "1", "0.75", "1", "0.888888888889", "0.056603773585")]
    [InlineData(PatternType.VwapReclaim, "0.657607192254", "0.437067773167", "1", "0.718533886584", "0.5", null)]
    [InlineData(PatternType.ShootingStar, "0.577142857143", "0.866666666667", "0", "0.914285714286", "0.5", "0")]
    [InlineData(PatternType.BearishEngulfing, "0.566666666667", "0.666666666667", "0", "1", "0.5", "0")]
    [InlineData(PatternType.DoubleTop, "0.766666666667", "1", "1", "0", "0.666666666667", "1")]
    [InlineData(PatternType.BreakdownAndRetest, "0.822522909507", "1", "0.75", "1", "0.888888888889", "0.484536082474")]
    [InlineData(PatternType.VwapReject, "0.554453723034", "0.219902574809", "1", "0.609951287404", "0.5", null)]
    public void StockPatternScoresRetainPreFxBaseline(PatternType type, string final,
        string geometry, string context, string confirmation, string volume, string? location)
    {
        var detector = new PatternDetector(FixtureOptions(), NullLogger<PatternDetector>.Instance);
        var direction = IsShort(type) ? TradeDirection.Short : TradeDirection.Long;
        var batch = detector.Process(new PatternDetectionInput
        {
            Candles = PatternSeries(type), StrategyId = "stock-baseline",
            Directions = [direction]
        });
        var candidate = Assert.Single(batch.Candidates, item => item.PatternType == type);
        Assert.Equal(Instrument, candidate.InstrumentId);
        Assert.Equal(direction, candidate.Direction);
        Assert.All(candidate.HardConditions, item => Assert.True(item.Passed, item.Reason));
        Near(decimal.Parse(final, CultureInfo.InvariantCulture), candidate.Confidence);
        var expected = new Dictionary<string, string>
        {
            ["geometryScore"] = geometry, ["contextScore"] = context,
            ["confirmationScore"] = confirmation, ["volumeScore"] = volume
        };
        if (location != null) expected.Add("locationScore", location);
        Assert.Equal(expected.Count, candidate.ScoreComponents.Count);
        foreach (var (code, value) in expected)
            Near(decimal.Parse(value, CultureInfo.InvariantCulture),
                Assert.Single(candidate.ScoreComponents, item => item.Code == code).Score);
    }

    [Fact]
    public void StockFeaturesRetainPreFxDefaultProfileBaseline()
    {
        var candles = Enumerable.Range(0, 40).Select(index =>
        {
            var close = 500m + index * 0.1m;
            return new Candle("SPY", Timeframe.OneMinute, End.AddMinutes(index - 39),
                close - 0.05m, close + 0.1m, close - 0.1m, close,
                1000m + index * 100m, Instrument);
        }).ToArray();
        var features = new FeatureEngine().ComputeFeatures(new CanonicalFeatureInput
        {
            Candles = candles, AsOfUtc = End,
            Bid = 503.89m, BidTimeUtc = End.AddMilliseconds(-200),
            Ask = 503.91m, AskTimeUtc = End.AddMilliseconds(-100)
        });
        Assert.Equal(40, features.SampleCount);
        Assert.Equal(Instrument, features.InstrumentId);
        Assert.Equal("SPY", features.Symbol);
        Assert.Equal(Timeframe.OneMinute, features.Timeframe);
        Assert.Equal(End, features.AsOfUtc);
        Assert.Equal(End, features.TimestampUtc);
        Near(503.35m, features.EmaShort);
        Near(502.65m, features.EmaLong);
        Near(100m, features.Rsi);
        Near(0.2m, features.Atr);
        Near(502.401694915254m, features.Vwap);
        Near(3950m, features.VolumeAverage);
        Near(1.272727272727m, features.VolumeRatio);
        Near(0.019849146487m, features.PriceChangePercent);
        Near(0.298228509161m, features.DistanceFromVwapPercent);
        Near(504m, features.RecentHigh);
        Near(501.9m, features.RecentLow);
        Assert.Equal(1, features.TrendDirection);
        Near(0.000000228045m, features.Volatility);
        Near(0.000396904148m, features.AtrToPriceRatio);
        Near(0.000022804480m, features.RealizedVolatilityPercent);
        Near(0.099324592769m, features.MomentumPercent);
        Near(1.647508942096m, features.MeanReversionZScore);
        Near(0.138916451677m, features.EmaSeparationPercent);
        Near(3.5m, features.EmaSeparationAtrRatio);
        Near(2469110m, features.DollarVolume);
        Near(1936305m, features.AverageDollarVolume);
        Near(1.275165844224m, features.NormalizedLiquidity);
        Near(0.02m, features.Spread);
        Near(0.396904147648m, features.SpreadBps);
        Near(0.1m, features.SpreadToAtrRatio);
        Assert.Equal(100L, features.QuoteAgeMilliseconds);
        Assert.Equal(MarketRegime.Trending, features.Regime);
    }

    [Theory]
    [InlineData(PatternType.VwapReclaim)]
    [InlineData(PatternType.VwapReject)]
    public void StockVwapPatternsRequireActualVolume(PatternType type)
    {
        var series = PatternSeries(type).Select(c => new Candle(c.Symbol, c.Timeframe,
            c.TimestampUtc, c.Open, c.High, c.Low, c.Close, 0m, c.InstrumentId)).ToArray();
        var features = new FeatureEngine().ComputeFeatures(series);
        var detector = new PatternDetector(FixtureOptions(), NullLogger<PatternDetector>.Instance);
        var evaluation = Assert.Single(detector.Evaluate(new PatternDetectionInput
        {
            Candles = series, Directions = [IsShort(type) ? TradeDirection.Short : TradeDirection.Long]
        }), item => item.PatternType == type);
        Assert.Null(features.Vwap);
        Assert.False(evaluation.Accepted);
        Assert.Contains(evaluation.HardConditions, item => item.Code == "validVwap" && !item.Passed);
    }

    private static bool IsShort(PatternType type) => type is PatternType.ShootingStar
        or PatternType.BearishEngulfing or PatternType.DoubleTop
        or PatternType.BreakdownAndRetest or PatternType.VwapReject;

    private static void Near(decimal expected, decimal? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, decimal.Round(actual.Value, 12));
    }

    private static PatternDetectorOptions FixtureOptions() => new()
    {
        DoubleBottom = { MaxLowDiffPercent = 0.05m, MinSeparation = 2, MaxSeparation = 6, MinReboundPercent = 0.05m },
        DoubleTop = { MaxLowDiffPercent = 0.05m, MinSeparation = 2, MaxSeparation = 6, MinReboundPercent = 0.05m },
        BreakoutAndRetest = { Lookback = 5, RetestTolerancePercent = 0.03m },
        BreakdownAndRetest = { Lookback = 5, RetestTolerancePercent = 0.03m }
    };

    private static IReadOnlyList<Candle> PatternSeries(PatternType type)
    {
        (decimal O, decimal H, decimal L, decimal C, decimal V)[] rows = type switch
        {
            PatternType.Hammer or PatternType.ShootingStar =>
                [(10m, 15m, 5m, 12m, 100m), (12m, 12.5m, 9m, 12.2m, 50m)],
            PatternType.BullishEngulfing or PatternType.BearishEngulfing =>
                [(15m, 16m, 14m, 14.5m, 100m), (14.4m, 16m, 14.4m, 16m, 200m)],
            PatternType.DoubleBottom or PatternType.DoubleTop =>
            [
                (10m, 11m, 9.5m, 10m, 100m), (10m, 11.5m, 9.6m, 11m, 100m),
                (11m, 11.2m, 9.55m, 10.2m, 100m), (10.5m, 11.8m, 9.5m, 11.5m, 100m),
                (11.6m, 12m, 9.52m, 10.1m, 100m), (10.2m, 11m, 9.6m, 10.5m, 100m)
            ],
            PatternType.BreakoutAndRetest or PatternType.BreakdownAndRetest =>
            [
                (10m, 10.5m, 9.5m, 10m, 100m), (10m, 10.6m, 9.6m, 10.1m, 100m),
                (10.1m, 10.55m, 9.7m, 10.2m, 100m), (10.2m, 10.58m, 9.8m, 10.3m, 100m),
                (10.3m, 10.57m, 9.9m, 10.4m, 100m), (10.4m, 11m, 10.2m, 11m, 200m),
                (11m, 11.1m, 10.9m, 11.05m, 100m), (10.95m, 11m, 10.9m, 10.99m, 100m),
                (11.05m, 11.2m, 11m, 11.15m, 150m)
            ],
            PatternType.VwapReclaim or PatternType.VwapReject =>
                [(9.8m, 10m, 9.5m, 9.7m, 100m), (9.7m, 9.9m, 9.4m, 9.6m, 100m),
                    (9.8m, 10.8m, 9.7m, 10.7m, 200m)],
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        return rows.Select((r, i) => IsShort(type)
            ? new Candle("SPY", Timeframe.OneMinute, End.AddMinutes(i - rows.Length + 1),
                30m - r.O, 30m - r.L, 30m - r.H, 30m - r.C, r.V, Instrument)
            : new Candle("SPY", Timeframe.OneMinute, End.AddMinutes(i - rows.Length + 1),
                r.O, r.H, r.L, r.C, r.V, Instrument)).ToArray();
    }
}
