using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public class FeatureEngine : IFeatureEngine
    {
        private readonly int _emaShortPeriod;
        private readonly int _emaLongPeriod;
        private readonly int _rsiPeriod;
        private readonly int _atrPeriod;
        private readonly int _volumeAvgPeriod;

        public FeatureEngine(int emaShort = 12, int emaLong = 26, int rsi = 14, int atr = 14, int volAvg = 20)
        {
            _emaShortPeriod = emaShort;
            _emaLongPeriod = emaLong;
            _rsiPeriod = rsi;
            _atrPeriod = atr;
            _volumeAvgPeriod = volAvg;
        }

        public MarketFeatures ComputeFeatures(IReadOnlyList<Candle> candles)
        {
            if (candles == null || candles.Count == 0) throw new ArgumentException("candles required");

            var last = candles[^1];
            var emaShort = ComputeEma(candles, _emaShortPeriod);
            var emaLong = ComputeEma(candles, _emaLongPeriod);
            var rsi = ComputeRsi(candles, _rsiPeriod);
            var atr = ComputeAtr(candles, _atrPeriod);
            var vwap = ComputeVwap(candles);
            var volAvg = ComputeVolumeAverage(candles, _volumeAvgPeriod);
            var volRatio = ComputeVolumeRatio(candles, _volumeAvgPeriod);
            var priceChange = ComputePriceChangePercent(candles);
            var distVwap = ComputeDistanceFromVwapPercent(candles);
            var recentHigh = ComputeRecentHigh(candles, _volumeAvgPeriod);
            var recentLow = ComputeRecentLow(candles, _volumeAvgPeriod);
            var volatility = ComputeVolatility(candles, _volumeAvgPeriod);
            var trend = (emaShort == null || emaLong == null) ? null : (int?)(emaShort > emaLong ? 1 : emaShort < emaLong ? -1 : 0);

            return new MarketFeatures
            {
                TimestampUtc = last.TimestampUtc,
                Symbol = last.Symbol,
                EmaShort = emaShort,
                EmaLong = emaLong,
                Rsi = rsi,
                Atr = atr,
                Vwap = vwap,
                VolumeAverage = volAvg,
                VolumeRatio = volRatio,
                PriceChangePercent = priceChange,
                DistanceFromVwapPercent = distVwap,
                RecentHigh = recentHigh,
                RecentLow = recentLow,
                TrendDirection = trend,
                Volatility = volatility
            };
        }

        private decimal? ComputeEma(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            var closes = candles.Select(c => c.Close).ToList();
            // initial SMA
            decimal sma = closes.Take(period).Sum() / period;
            decimal prev = sma;
            decimal alpha = 2m / (period + 1);
            for (int i = period; i < closes.Count; i++)
            {
                prev = (closes[i] - prev) * alpha + prev;
            }
            return prev;
        }

        private decimal? ComputeRsi(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            var closes = candles.Select(c => c.Close).ToList();
            decimal gain = 0m, loss = 0m;
            for (int i = 1; i <= period; i++)
            {
                var change = closes[i] - closes[i - 1];
                if (change > 0) gain += change; else loss -= Math.Min(change, 0);
            }
            decimal avgGain = gain / period;
            decimal avgLoss = loss / period;

            // continue smoothing
            for (int i = period + 1; i < closes.Count; i++)
            {
                var change = closes[i] - closes[i - 1];
                decimal g = change > 0 ? change : 0;
                decimal l = change < 0 ? -change : 0;
                avgGain = (avgGain * (period - 1) + g) / period;
                avgLoss = (avgLoss * (period - 1) + l) / period;
            }

            if (avgLoss == 0m) return 100m;
            var rs = avgGain / avgLoss;
            var rsi = 100m - (100m / (1 + rs));
            return rsi;
        }

        private decimal? ComputeAtr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            var trs = new List<decimal>();
            for (int i = 1; i < candles.Count; i++)
            {
                var c = candles[i];
                var prev = candles[i - 1];
                var tr = Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prev.Close), Math.Abs(c.Low - prev.Close)));
                trs.Add(tr);
            }
            if (trs.Count < period) return null;
            var recent = trs.Skip(trs.Count - period).Take(period);
            return recent.Sum() / period;
        }

        private decimal? ComputeVwap(IReadOnlyList<Candle> candles)
        {
            if (candles.Count == 0) return null;
            decimal pv = 0m, v = 0m;
            foreach (var c in candles)
            {
                var typical = (c.High + c.Low + c.Close) / 3m;
                pv += typical * c.Volume;
                v += c.Volume;
            }
            if (v == 0) return null;
            return pv / v;
        }

        private decimal? ComputeVolumeAverage(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            var recent = candles.Skip(Math.Max(0, candles.Count - period)).Select(c => c.Volume);
            return recent.Average();
        }

        private decimal? ComputeVolumeRatio(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period + 1) return null;
            var last = candles[^1].Volume;
            var avg = candles.Skip(candles.Count - period - 1).Take(period).Select(c => c.Volume).Average();
            if (avg == 0) return null;
            return last / avg;
        }

        private decimal? ComputePriceChangePercent(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < 2) return null;
            var last = candles[^1].Close;
            var prev = candles[^2].Close;
            if (prev == 0) return null;
            return (last - prev) / prev * 100m;
        }

        private decimal? ComputeDistanceFromVwapPercent(IReadOnlyList<Candle> candles)
        {
            var vwap = ComputeVwap(candles);
            if (vwap == null || vwap == 0) return null;
            var last = candles[^1].Close;
            return (last - vwap.Value) / vwap.Value * 100m;
        }

        private decimal? ComputeRecentHigh(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            return candles.Skip(Math.Max(0, candles.Count - period)).Max(c => c.High);
        }

        private decimal? ComputeRecentLow(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            return candles.Skip(Math.Max(0, candles.Count - period)).Min(c => c.Low);
        }

        private int? ComputeTrendDirection(MarketFeatures f)
        {
            if (f.EmaShort == null || f.EmaLong == null) return null;
            if (f.EmaShort > f.EmaLong) return 1;
            if (f.EmaShort < f.EmaLong) return -1;
            return 0;
        }

        private decimal? ComputeVolatility(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period + 1) return null;
            var returns = new List<decimal>();
            for (int i = candles.Count - period; i < candles.Count; i++)
            {
                if (i <= 0) continue;
                var r = (candles[i].Close - candles[i - 1].Close) / candles[i - 1].Close;
                returns.Add(r);
            }
            if (returns.Count == 0) return null;
            var avg = returns.Average();
            var varsum = returns.Sum(r => (r - avg) * (r - avg));
            var sd = Math.Sqrt((double)(varsum / returns.Count));
            return (decimal)sd;
        }
    }
}
