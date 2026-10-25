using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class FeatureEngine : IFeatureEngine
    {
        private readonly CanonicalFeatureSettings _settings;
        private readonly FacioQuoIndicatorAdapter _indicators = new();

        public FeatureEngine(CanonicalFeatureSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            var errors = _settings.GetValidationErrors();
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));

            var canonicalSettings = JsonSerializer.Serialize(_settings, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var settingsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSettings))).ToLowerInvariant();
            FeatureVersion = $"{PipelineContractVersions.Features}+config-{settingsHash}";
            ForexFeatureVersion = $"features-fx-midpoint-v1+config-{settingsHash}";
        }

        public FeatureEngine(int emaShort = 12, int emaLong = 26, int rsi = 14, int atr = 14, int volAvg = 20)
            : this(new CanonicalFeatureSettings
            {
                EmaShortPeriod = emaShort,
                EmaLongPeriod = emaLong,
                RsiPeriod = rsi,
                AtrPeriod = atr,
                VolumeAveragePeriod = volAvg
            })
        {
        }

        public string FeatureVersion { get; }
        public string ForexFeatureVersion { get; }

        public MarketFeatures ComputeFeatures(IReadOnlyList<Candle> candles)
        {
            if (candles == null || candles.Count == 0) throw new ArgumentException("candles required", nameof(candles));
            return ComputeFeatures(new CanonicalFeatureInput
            {
                Candles = candles,
                AsOfUtc = candles.Max(item => item.TimestampUtc)
            });
        }

        public MarketFeatures ComputeFeatures(CanonicalFeatureInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (input.Candles == null || input.Candles.Count == 0) throw new ArgumentException("candles required", nameof(input));
            if (input.AsOfUtc == default) throw new ArgumentException("AsOfUtc is required", nameof(input));

            var asOfUtc = ToUtc(input.AsOfUtc);
            var candles = input.Candles
                .Where(item => item.TimestampUtc <= asOfUtc
                    && (!input.ForexMidpoint || item.ReceivedTimeUtc <= asOfUtc && item.IsFinal && item.QualityStatus == MarketDataQualityStatus.Healthy))
                .GroupBy(item => item.TimestampUtc)
                .Select(group => group
                    .OrderBy(item => item.ReceivedTimeUtc)
                    .ThenBy(item => item.Source, StringComparer.Ordinal)
                    .ThenBy(item => item.Open)
                    .ThenBy(item => item.High)
                    .ThenBy(item => item.Low)
                    .ThenBy(item => item.Close)
                    .ThenBy(item => item.Volume)
                    .Last())
                .OrderBy(item => item.TimestampUtc)
                .ToArray();
            if (candles.Length == 0) throw new InvalidOperationException("No candles are available at or before AsOfUtc.");
            if (!input.ForexMidpoint && candles.Any(item => !item.HasTradeVolume))
                throw new NotSupportedException("Missing trade volume requires the explicit ForexMidpoint feature contract.");
            if (input.ForexMidpoint && candles.Any(item => item.HasTradeVolume
                || !(item.Source.StartsWith(ForexQuoteAggregator.CandleSource, StringComparison.Ordinal) || item.Source == "IBKR.Midpoint")))
                throw new ArgumentException("ForexMidpoint requires quote-derived bars with unavailable trade volume.", nameof(input));

            ValidateSeriesIdentity(candles);
            if (input.ForexMidpoint)
            {
                if (candles.Any(x => x.Source != candles[0].Source))
                    throw new ArgumentException("Forex feature input cannot mix historical midpoint and quote aggregate contracts.", nameof(input));
                var interval = Interval(candles[^1].Timeframe);
                var first = candles.Length - 1;
                while (first > 0 && candles[first].TimestampUtc - candles[first - 1].TimestampUtc == interval) first--;
                candles = candles[first..]; // A gap or session break restarts warm-up; never bridge missing bars.
            }
            var last = candles[^1];
            var verified = _indicators.Compute(candles, _settings.EmaShortPeriod, _settings.EmaLongPeriod,
                _settings.RsiPeriod, _settings.AtrPeriod, includeAtr: false);
            var emaShort = verified.EmaShort;
            var emaLong = verified.EmaLong;
            var rsi = verified.Rsi;
            var atr = ComputeAtr(candles, _settings.AtrPeriod);
            var vwap = input.ForexMidpoint ? null : ComputeVwap(candles);
            var volumeAverage = input.ForexMidpoint ? null : ComputeVolumeAverage(candles, _settings.VolumeAveragePeriod);
            var volumeRatio = input.ForexMidpoint ? null : ComputeVolumeRatio(candles, _settings.VolumeAveragePeriod);
            var priceChangePercent = ComputeReturnPercent(candles, 1);
            var momentumPercent = ComputeReturnPercent(candles, _settings.MomentumLookbackCandles);
            var distanceFromVwapPercent = PercentageDistance(last.Close, vwap);
            var recentHigh = ComputeRecentHigh(candles, _settings.VolumeAveragePeriod);
            var recentLow = ComputeRecentLow(candles, _settings.VolumeAveragePeriod);
            var volatility = ComputeVolatility(candles, _settings.VolumeAveragePeriod);
            int? trend = emaShort == null || emaLong == null ? null : emaShort > emaLong ? 1 : emaShort < emaLong ? -1 : 0;
            decimal? atrToPriceRatio = atr.HasValue && last.Close > 0m ? atr.Value / last.Close : null;
            decimal? emaSeparationPercent = emaShort.HasValue && emaLong.HasValue && last.Close > 0m
                ? (emaShort.Value - emaLong.Value) / last.Close * 100m
                : null;
            decimal? emaSeparationAtrRatio = emaShort.HasValue && emaLong.HasValue && atr is > 0m
                ? (emaShort.Value - emaLong.Value) / atr.Value
                : null;
            var meanReversionZScore = ComputeMeanReversionZScore(candles, _settings.MeanReversionLookbackCandles);
            decimal? dollarVolume = input.ForexMidpoint ? null : last.Close * last.Volume;
            var averageDollarVolume = input.ForexMidpoint ? null : ComputePriorAverageDollarVolume(candles, _settings.VolumeAveragePeriod);
            decimal? normalizedLiquidity = averageDollarVolume is > 0m ? dollarVolume / averageDollarVolume.Value : null;
            var spreadContext = ResolveSpread(input, last, asOfUtc, atr);
            var (regime, regimeReason) = ClassifyRegime(
                atrToPriceRatio,
                emaSeparationAtrRatio,
                momentumPercent,
                meanReversionZScore);

            return new MarketFeatures
            {
                FeatureVersion = input.ForexMidpoint ? $"{ForexFeatureVersion}+basis-{last.Source}" : FeatureVersion,
                PriceBasis = input.ForexMidpoint ? "QuoteMidpoint" : "Trade",
                PriceFeaturesReady = input.ForexMidpoint ? emaShort.HasValue && emaLong.HasValue && rsi.HasValue
                    && atr.HasValue && volatility.HasValue && momentumPercent.HasValue && meanReversionZScore.HasValue : null,
                UnavailableFeatures = input.ForexMidpoint ? Unavailable(emaShort, emaLong, rsi, atr, volatility, momentumPercent, meanReversionZScore, spreadContext.Spread) : null,
                InstrumentId = last.InstrumentId,
                TimestampUtc = last.TimestampUtc,
                AsOfUtc = asOfUtc,
                Symbol = last.Symbol,
                Timeframe = last.Timeframe,
                SampleCount = candles.Length,
                EmaShort = emaShort,
                EmaLong = emaLong,
                Rsi = rsi,
                Atr = atr,
                Vwap = vwap,
                VolumeAverage = volumeAverage,
                VolumeRatio = volumeRatio,
                PriceChangePercent = priceChangePercent,
                DistanceFromVwapPercent = distanceFromVwapPercent,
                RecentHigh = recentHigh,
                RecentLow = recentLow,
                TrendDirection = trend,
                Volatility = volatility,
                AtrToPriceRatio = atrToPriceRatio,
                RealizedVolatilityPercent = volatility * 100m,
                MomentumPercent = momentumPercent,
                MeanReversionZScore = meanReversionZScore,
                EmaSeparationPercent = emaSeparationPercent,
                EmaSeparationAtrRatio = emaSeparationAtrRatio,
                DollarVolume = dollarVolume,
                AverageDollarVolume = averageDollarVolume,
                NormalizedLiquidity = normalizedLiquidity,
                Spread = spreadContext.Spread,
                SpreadBps = spreadContext.SpreadBps,
                SpreadToAtrRatio = spreadContext.SpreadToAtrRatio,
                QuoteAgeMilliseconds = spreadContext.QuoteAgeMilliseconds,
                Regime = regime,
                RegimeReason = regimeReason
            };
        }

        private static void ValidateSeriesIdentity(IReadOnlyList<Candle> candles)
        {
            var first = candles[0];
            if (candles.Any(item => !item.Symbol.Equals(first.Symbol, StringComparison.OrdinalIgnoreCase)
                    || !item.InstrumentId.Equals(first.InstrumentId, StringComparison.OrdinalIgnoreCase)
                    || item.Timeframe != first.Timeframe))
            {
                throw new ArgumentException("Canonical feature input must contain one instrument, symbol and timeframe.", nameof(candles));
            }
        }

        internal static TimeSpan Interval(Timeframe timeframe) => timeframe switch
        {
            Timeframe.OneSecond => TimeSpan.FromSeconds(1), Timeframe.FiveSeconds => TimeSpan.FromSeconds(5),
            Timeframe.FifteenSeconds => TimeSpan.FromSeconds(15), Timeframe.OneMinute => TimeSpan.FromMinutes(1),
            Timeframe.FiveMinutes => TimeSpan.FromMinutes(5), Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
            Timeframe.OneHour => TimeSpan.FromHours(1), Timeframe.FourHour => TimeSpan.FromHours(4),
            Timeframe.Daily => TimeSpan.FromDays(1), _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        private static IReadOnlyDictionary<string, string> Unavailable(decimal? shortEma, decimal? longEma, decimal? rsi,
            decimal? atr, decimal? volatility, decimal? momentum, decimal? zScore, decimal? spread)
        {
            var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in new[] { "Vwap", "VolumeAverage", "VolumeRatio", "DistanceFromVwapPercent", "DollarVolume", "AverageDollarVolume", "NormalizedLiquidity" })
                reasons[name] = "Trade volume is unavailable for FX quotes; quote count and size are not substitutes.";
            foreach (var item in new (string Name, decimal? Value)[] { ("EmaShort", shortEma), ("EmaLong", longEma), ("Rsi", rsi), ("Atr", atr), ("RealizedVolatilityPercent", volatility), ("MomentumPercent", momentum), ("MeanReversionZScore", zScore) })
                if (!item.Value.HasValue) reasons[item.Name] = "Insufficient consecutive final bars for warm-up.";
            if (!spread.HasValue) reasons["Spread"] = "A complete, non-crossed, fresh as-of quote is unavailable.";
            return reasons;
        }

        internal static decimal? ComputeEma(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            var value = candles.Take(period).Average(item => item.Close);
            var alpha = 2m / (period + 1m);
            for (var index = period; index < candles.Count; index++)
            {
                value = (candles[index].Close - value) * alpha + value;
            }
            return value;
        }

        internal static decimal? ComputeRsi(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            decimal gain = 0m;
            decimal loss = 0m;
            for (var index = 1; index <= period; index++)
            {
                var change = candles[index].Close - candles[index - 1].Close;
                if (change > 0m) gain += change;
                else if (change < 0m) loss -= change;
            }

            var averageGain = gain / period;
            var averageLoss = loss / period;
            for (var index = period + 1; index < candles.Count; index++)
            {
                var change = candles[index].Close - candles[index - 1].Close;
                var currentGain = change > 0m ? change : 0m;
                var currentLoss = change < 0m ? -change : 0m;
                averageGain = (averageGain * (period - 1) + currentGain) / period;
                averageLoss = (averageLoss * (period - 1) + currentLoss) / period;
            }

            if (averageLoss == 0m) return averageGain == 0m ? 50m : 100m;
            var relativeStrength = averageGain / averageLoss;
            return 100m - 100m / (1m + relativeStrength);
        }

        internal static decimal? ComputeAtr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            var trueRanges = new List<decimal>(candles.Count - 1);
            for (var index = 1; index < candles.Count; index++)
            {
                var current = candles[index];
                var previous = candles[index - 1];
                trueRanges.Add(Math.Max(
                    current.High - current.Low,
                    Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
            }
            return trueRanges.TakeLast(period).Average();
        }

        private static decimal? ComputeVwap(IReadOnlyList<Candle> candles)
        {
            decimal priceVolume = 0m;
            decimal volume = 0m;
            foreach (var candle in candles)
            {
                var typicalPrice = (candle.High + candle.Low + candle.Close) / 3m;
                priceVolume += typicalPrice * candle.Volume;
                volume += candle.Volume;
            }
            return volume == 0m ? null : priceVolume / volume;
        }

        private static decimal? ComputeVolumeAverage(IReadOnlyList<Candle> candles, int period) =>
            candles.Count < period ? null : candles.TakeLast(period).Average(item => item.Volume);

        private static decimal? ComputeVolumeRatio(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period + 1) return null;
            var average = candles.Skip(candles.Count - period - 1).Take(period).Average(item => item.Volume);
            return average == 0m ? null : candles[^1].Volume / average;
        }

        private static decimal? ComputeReturnPercent(IReadOnlyList<Candle> candles, int lookback)
        {
            if (candles.Count <= lookback) return null;
            var prior = candles[candles.Count - 1 - lookback].Close;
            return prior == 0m ? null : (candles[^1].Close - prior) / prior * 100m;
        }

        private static decimal? PercentageDistance(decimal value, decimal? reference) =>
            reference is null or 0m ? null : (value - reference.Value) / reference.Value * 100m;

        private static decimal? ComputeRecentHigh(IReadOnlyList<Candle> candles, int period) =>
            candles.Count < period ? null : candles.TakeLast(period).Max(item => item.High);

        private static decimal? ComputeRecentLow(IReadOnlyList<Candle> candles, int period) =>
            candles.Count < period ? null : candles.TakeLast(period).Min(item => item.Low);

        private static decimal? ComputeVolatility(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period + 1) return null;
            var returns = new List<decimal>(period);
            for (var index = candles.Count - period; index < candles.Count; index++)
            {
                var prior = candles[index - 1].Close;
                if (prior == 0m) continue;
                returns.Add((candles[index].Close - prior) / prior);
            }
            if (returns.Count == 0) return null;
            var average = returns.Average();
            var variance = returns.Average(value => (value - average) * (value - average));
            return (decimal)Math.Sqrt((double)variance);
        }

        private static decimal? ComputeMeanReversionZScore(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period) return null;
            var closes = candles.TakeLast(period).Select(item => item.Close).ToArray();
            var average = closes.Average();
            var variance = closes.Average(value => (value - average) * (value - average));
            if (variance == 0m) return 0m;
            var standardDeviation = (decimal)Math.Sqrt((double)variance);
            return (candles[^1].Close - average) / standardDeviation;
        }

        private static decimal? ComputePriorAverageDollarVolume(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count < period + 1) return null;
            return candles.Skip(candles.Count - period - 1)
                .Take(period)
                .Average(item => item.Close * item.Volume);
        }

        private SpreadContext ResolveSpread(CanonicalFeatureInput input, Candle last, DateTime asOfUtc, decimal? atr)
        {
            decimal? spread = null;
            DateTime? quoteTimeUtc = null;
            decimal? referencePrice = null;

            if (input.Spread is >= 0m && (!input.ForexMidpoint || input.SpreadTimeUtc.HasValue))
            {
                var spreadTimeUtc = input.SpreadTimeUtc.HasValue ? ToUtc(input.SpreadTimeUtc.Value) : asOfUtc;
                if (IsUsableQuoteTime(spreadTimeUtc, asOfUtc))
                {
                    spread = input.Spread;
                    quoteTimeUtc = spreadTimeUtc;
                }
            }
            else if (input.Bid is > 0m && input.Ask is > 0m && input.Ask >= input.Bid
                && input.BidTimeUtc.HasValue && input.AskTimeUtc.HasValue)
            {
                var bidTimeUtc = ToUtc(input.BidTimeUtc.Value);
                var askTimeUtc = ToUtc(input.AskTimeUtc.Value);
                if (IsUsableQuoteTime(bidTimeUtc, asOfUtc) && IsUsableQuoteTime(askTimeUtc, asOfUtc))
                {
                    spread = input.Ask.Value - input.Bid.Value;
                    quoteTimeUtc = input.ForexMidpoint
                        ? (bidTimeUtc < askTimeUtc ? bidTimeUtc : askTimeUtc)
                        : (bidTimeUtc > askTimeUtc ? bidTimeUtc : askTimeUtc);
                    referencePrice = (input.Bid.Value + input.Ask.Value) / 2m;
                }
            }

            if (!input.ForexMidpoint && !referencePrice.HasValue && input.LastTrade is > 0m && input.LastTradeTimeUtc.HasValue)
            {
                var tradeTimeUtc = ToUtc(input.LastTradeTimeUtc.Value);
                if (IsUsableQuoteTime(tradeTimeUtc, asOfUtc)) referencePrice = input.LastTrade;
            }
            referencePrice ??= last.Close > 0m ? last.Close : null;

            decimal? spreadBps = spread.HasValue && referencePrice is > 0m ? spread.Value / referencePrice.Value * 10_000m : null;
            decimal? spreadToAtr = spread.HasValue && atr is > 0m ? spread.Value / atr.Value : null;
            var quoteAgeMilliseconds = quoteTimeUtc.HasValue
                ? (long)Math.Round((asOfUtc - quoteTimeUtc.Value).TotalMilliseconds, MidpointRounding.AwayFromZero)
                : (long?)null;
            return new SpreadContext(spread, spreadBps, spreadToAtr, quoteAgeMilliseconds);
        }

        private bool IsUsableQuoteTime(DateTime quoteTimeUtc, DateTime asOfUtc) =>
            quoteTimeUtc <= asOfUtc && asOfUtc - quoteTimeUtc <= TimeSpan.FromSeconds(_settings.MaximumQuoteAgeSeconds);

        private (MarketRegime Regime, string Reason) ClassifyRegime(
            decimal? atrToPriceRatio,
            decimal? emaSeparationAtrRatio,
            decimal? momentumPercent,
            decimal? meanReversionZScore)
        {
            if (atrToPriceRatio >= _settings.VolatileAtrToPriceRatio)
            {
                return (MarketRegime.Volatile, "ATR-to-price ratio is above the configured volatile threshold.");
            }

            if (emaSeparationAtrRatio.HasValue && momentumPercent.HasValue
                && Math.Abs(emaSeparationAtrRatio.Value) >= _settings.TrendingEmaSeparationAtrRatio
                && ((emaSeparationAtrRatio > 0m && momentumPercent > 0m)
                    || (emaSeparationAtrRatio < 0m && momentumPercent < 0m)))
            {
                return (MarketRegime.Trending, "EMA separation is material relative to ATR and agrees with momentum.");
            }

            if (meanReversionZScore.HasValue)
            {
                return (MarketRegime.Ranging, "Sufficient history exists without aligned trend or volatility expansion.");
            }

            return (MarketRegime.Unknown, "Insufficient warm-up data for deterministic regime classification.");
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private sealed record SpreadContext(
            decimal? Spread,
            decimal? SpreadBps,
            decimal? SpreadToAtrRatio,
            long? QuoteAgeMilliseconds);
    }
}
