using FacioQuo.Stock.Indicators;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services;

public sealed record FacioQuoIndicatorReadings(decimal? EmaShort, decimal? EmaLong, decimal? Rsi, decimal? Atr);

/// <summary>Isolates the library's bar contract and double precision from canonical decimal features.</summary>
public sealed class FacioQuoIndicatorAdapter
{
    public FacioQuoIndicatorReadings Compute(
        IReadOnlyList<Candle> canonicalCandles,
        int emaShortPeriod,
        int emaLongPeriod,
        int rsiPeriod,
        int atrPeriod,
        bool includeAtr = true)
    {
        ArgumentNullException.ThrowIfNull(canonicalCandles);
        if (canonicalCandles.Count == 0) throw new ArgumentException("Candles are required.", nameof(canonicalCandles));
        var bars = canonicalCandles.Select(candle => new Bar(
            candle.TimestampUtc, candle.Open, candle.High, candle.Low, candle.Close, candle.Volume)).ToArray();
        IReadOnlyList<IReusable> values = bars;
        IReadOnlyList<IBar> priceBars = bars;
        return new FacioQuoIndicatorReadings(
            ConvertFinite(values.ToEma(emaShortPeriod)[^1].Ema),
            ConvertFinite(values.ToEma(emaLongPeriod)[^1].Ema),
            ConvertFinite(values.ToRsi(rsiPeriod)[^1].Rsi),
            includeAtr ? ConvertFinite(priceBars.ToAtr(atrPeriod)[^1].Atr) : null);
    }

    private static decimal? ConvertFinite(double? value) => value is double number && double.IsFinite(number)
        ? Convert.ToDecimal(number)
        : null;
}
