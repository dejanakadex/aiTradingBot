using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public class PatternDetector : IPatternDetector
    {
        private readonly PatternDetectorOptions _opts;
        private readonly ILogger<PatternDetector> _logger;
        private readonly Dictionary<(string, PatternType), DateTime> _lastDetected = new();

        public PatternDetector(PatternDetectorOptions? opts, ILogger<PatternDetector> logger)
        {
            _opts = opts ?? new PatternDetectorOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<Candle> candles)
        {
            var results = new List<PatternCandidate>();
            if (candles == null || candles.Count == 0) return results;
            var last = candles[^1];

            AddIfFresh(results, DetectHammer(candles), last.Symbol, PatternType.Hammer, last.TimestampUtc);
            AddIfFresh(results, DetectBullishEngulfing(candles), last.Symbol, PatternType.BullishEngulfing, last.TimestampUtc);
            AddIfFresh(results, DetectDoubleBottom(candles), last.Symbol, PatternType.DoubleBottom, last.TimestampUtc);
            AddIfFresh(results, DetectBreakoutAndRetest(candles), last.Symbol, PatternType.BreakoutAndRetest, last.TimestampUtc);
            AddIfFresh(results, DetectVwapReclaim(candles), last.Symbol, PatternType.VwapReclaim, last.TimestampUtc);

            foreach (var result in results)
            {
                _lastDetected[(result.Symbol, result.PatternType)] = result.DetectedAtUtc;
            }

            return results;
        }

        private void AddIfFresh(List<PatternCandidate> results, PatternCandidate? candidate, string symbol, PatternType type, DateTime latestTimestamp)
        {
            if (candidate == null) return;
            if (!NotDuplicate(symbol, type, latestTimestamp)) return;
            results.Add(candidate);
        }

        private bool NotDuplicate(string symbol, PatternType type, DateTime detectedAt)
        {
            if (_lastDetected.TryGetValue((symbol, type), out var last)
                && (detectedAt - last).TotalSeconds < Math.Max(1, _opts.RecentCandlesForDedup) * 60)
            {
                _logger.LogDebug("Duplicate detection suppressed for {Symbol} {Pattern}", symbol, type);
                return false;
            }

            return true;
        }

        private PatternCandidate? DetectHammer(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < 2) return null;
            var c = candles[^1];
            var range = c.High - c.Low;
            if (range <= 0m) return null;

            var body = Math.Abs(c.Close - c.Open);
            var upperWick = c.High - Math.Max(c.Close, c.Open);
            var lowerWick = Math.Min(c.Close, c.Open) - c.Low;
            var closePosition = (c.Close - c.Low) / range;
            var bodyRatio = body / range;
            var lowerWickRatio = lowerWick / range;
            var upperWickRatio = upperWick / range;

            var geometry = Average(
                ScoreAtMost(bodyRatio, _opts.Hammer.MaxBodyToRange),
                ScoreAtLeast(lowerWickRatio, _opts.Hammer.MinLowerWickToRange),
                ScoreAtMost(upperWickRatio, _opts.Hammer.MaxUpperWickToRange),
                ScoreAtLeast(closePosition, _opts.Hammer.MinClosePositionInRange));

            if (geometry <= 0m) return null;

            var context = PullbackScore(candles, _opts.Hammer.PullbackLookbackCandles, _opts.Hammer.MinPullbackPercent);
            var location = SupportLocationScore(candles, c.Low, _opts.Hammer.SupportProximityPercent);
            var volume = VolumeScore(candles);
            var confirmation = closePosition;
            var final = Clamp01(geometry * 0.45m + context * 0.2m + confirmation * 0.15m + volume * 0.1m + location * 0.1m);

            return Candidate(
                PatternType.Hammer,
                c,
                final,
                new[] { c.Low, Math.Min(c.Open, c.Close), c.High },
                geometry,
                context,
                confirmation,
                volume,
                location);
        }

        private PatternCandidate? DetectBullishEngulfing(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < 2) return null;
            var first = candles[^2];
            var second = candles[^1];
            if (!(first.Close < first.Open && second.Close > second.Open)) return null;

            var firstBody = Math.Abs(first.Close - first.Open);
            var secondBody = Math.Abs(second.Close - second.Open);
            if (firstBody <= 0m || secondBody <= 0m) return null;

            var engulfs = second.Open <= first.Close && second.Close >= first.Open;
            if (!engulfs) return null;

            var atr = Atr(candles, 14);
            var bodyRatio = secondBody / firstBody;
            var geometry = Average(
                ScoreAtLeast(bodyRatio, _opts.BullishEngulfing.MinBodyRatio),
                atr.HasValue ? ScoreAtLeast(firstBody / atr.Value, _opts.BullishEngulfing.MinFirstBodyToAtr) : 0.5m,
                atr.HasValue ? ScoreAtLeast(secondBody / atr.Value, _opts.BullishEngulfing.MinSecondBodyToAtr) : 0.5m);
            var context = PullbackScore(candles, _opts.BullishEngulfing.PullbackLookbackCandles, _opts.BullishEngulfing.MinPullbackPercent);
            var confirmation = second.Close > first.High ? 1m : ScoreAtLeast(second.Close, first.Open);
            var volume = VolumeScore(candles);
            var location = SupportLocationScore(candles, second.Low, 0.004m);
            var final = Clamp01(geometry * 0.55m + context * 0.15m + confirmation * 0.15m + volume * 0.1m + location * 0.05m);
            final = Math.Min(final, Clamp01(geometry + 0.15m));

            return Candidate(
                PatternType.BullishEngulfing,
                second,
                final,
                new[] { first.Low, first.High, second.Low, second.High },
                geometry,
                context,
                confirmation,
                volume,
                location);
        }

        private PatternCandidate? DetectDoubleBottom(IReadOnlyList<Candle> candles)
        {
            var n = candles.Count;
            if (n < _opts.DoubleBottom.MinSeparation + 2) return null;
            var latest = candles[^1];

            PatternCandidate? best = null;
            decimal bestQuality = -1m;
            var secondLowMaxIndex = n - 1;
            var secondLowMinIndex = Math.Max(0, n - _opts.DoubleBottom.MaxSeparation - 2);

            for (var secondLowIndex = secondLowMaxIndex; secondLowIndex >= secondLowMinIndex; secondLowIndex--)
            {
                for (var firstLowIndex = secondLowIndex - _opts.DoubleBottom.MaxSeparation;
                     firstLowIndex <= secondLowIndex - _opts.DoubleBottom.MinSeparation;
                     firstLowIndex++)
                {
                    if (firstLowIndex < 0) continue;
                    var low1 = candles[firstLowIndex].Low;
                    var low2 = candles[secondLowIndex].Low;
                    if (low1 <= 0m || low2 <= 0m) continue;

                    var lowDiff = Math.Abs(low1 - low2) / Math.Max(low1, low2);
                    if (lowDiff > _opts.DoubleBottom.MaxLowDiffPercent) continue;

                    var middle = candles.Skip(firstLowIndex + 1).Take(secondLowIndex - firstLowIndex - 1).ToArray();
                    if (middle.Length == 0) continue;

                    var neckline = middle.Max(c => c.High);
                    var rebound = (neckline - Math.Max(low1, low2)) / Math.Max(low1, low2);
                    if (rebound < _opts.DoubleBottom.MinReboundPercent) continue;

                    var afterSecondLow = candles.Skip(secondLowIndex + 1).ToArray();
                    var necklineBreak = afterSecondLow.Any(c => c.Close > neckline);
                    var confirmationCandle = latest.Close > candles[secondLowIndex].Close
                        && latest.Close >= neckline * (1m - _opts.DoubleBottom.ConfirmationTolerancePercent);
                    var confirmation = necklineBreak ? 1m : confirmationCandle ? 0.55m : 0m;
                    var spacing = ScoreRange(secondLowIndex - firstLowIndex, _opts.DoubleBottom.MinSeparation, _opts.DoubleBottom.MaxSeparation);
                    var similarity = ScoreAtMost(lowDiff, _opts.DoubleBottom.MaxLowDiffPercent);
                    var reboundScore = ScoreAtLeast(rebound, _opts.DoubleBottom.MinReboundPercent);
                    var volume = VolumeScore(candles);
                    var final = Clamp01(similarity * 0.3m + reboundScore * 0.25m + spacing * 0.15m + confirmation * 0.2m + volume * 0.1m);

                    if (final <= bestQuality) continue;
                    bestQuality = final;
                    best = Candidate(
                        PatternType.DoubleBottom,
                        latest,
                        final,
                        new[] { low1, low2, neckline },
                        similarity,
                        spacing,
                        confirmation,
                        volume,
                        reboundScore,
                        new Dictionary<string, string>
                        {
                            ["neckline"] = Format(neckline),
                            ["necklineBreak"] = necklineBreak.ToString(CultureInfo.InvariantCulture),
                            ["secondLowTimestampUtc"] = candles[secondLowIndex].TimestampUtc.ToString("O", CultureInfo.InvariantCulture)
                        });
                }
            }

            return best;
        }

        private PatternCandidate? DetectBreakoutAndRetest(IReadOnlyList<Candle> candles)
        {
            var n = candles.Count;
            var lookback = Math.Max(2, _opts.BreakoutAndRetest.Lookback);
            if (n <= lookback + 2) return null;

            var latest = candles[^1];
            for (var breakoutIndex = Math.Max(lookback, n - lookback - 8); breakoutIndex < n - 1; breakoutIndex++)
            {
                var prior = candles.Skip(breakoutIndex - lookback).Take(lookback).ToArray();
                var resistance = prior.Max(c => c.High);
                if (resistance <= 0m) continue;

                var breakoutClose = candles[breakoutIndex].Close;
                var breakoutMagnitude = (breakoutClose - resistance) / resistance;
                if (breakoutMagnitude < _opts.BreakoutAndRetest.MinBreakoutPercent) continue;

                for (var retestIndex = breakoutIndex + 1; retestIndex < n; retestIndex++)
                {
                    var retest = candles[retestIndex];
                    var retestDistance = Math.Abs(retest.Low - resistance) / resistance;
                    if (retestDistance > _opts.BreakoutAndRetest.RetestTolerancePercent) continue;

                    var bounce = (latest.Close - resistance) / resistance;
                    if (retestIndex == n - 1 || bounce < _opts.BreakoutAndRetest.MinBouncePercent) continue;

                    var range = latest.High - latest.Low;
                    var closePosition = range > 0m ? (latest.Close - latest.Low) / range : 0m;
                    var breakoutScore = ScoreAtLeast(breakoutMagnitude, _opts.BreakoutAndRetest.MinBreakoutPercent);
                    var retestScore = ScoreAtMost(retestDistance, _opts.BreakoutAndRetest.RetestTolerancePercent);
                    var bounceScore = ScoreAtLeast(bounce, _opts.BreakoutAndRetest.MinBouncePercent);
                    var volume = VolumeScore(candles);
                    var final = Clamp01(breakoutScore * 0.25m + retestScore * 0.25m + bounceScore * 0.25m + closePosition * 0.15m + volume * 0.1m);

                    return Candidate(
                        PatternType.BreakoutAndRetest,
                        latest,
                        final,
                        new[] { resistance, retest.Low, latest.Close },
                        breakoutScore,
                        retestScore,
                        bounceScore,
                        volume,
                        closePosition,
                        new Dictionary<string, string>
                        {
                            ["breakoutMagnitude"] = Format(breakoutMagnitude),
                            ["retestDistance"] = Format(retestDistance),
                            ["bouncePercent"] = Format(bounce)
                        });
                }
            }

            return null;
        }

        private PatternCandidate? DetectVwapReclaim(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < _opts.VwapReclaim.PriorBelowVwapCandles + 1) return null;
            var vwap = Vwap(candles);
            if (vwap == null || vwap <= 0m) return null;

            var latest = candles[^1];
            if (latest.Close <= vwap.Value) return null;

            var priorCount = Math.Max(1, _opts.VwapReclaim.PriorBelowVwapCandles);
            var priorBelow = candles.Skip(candles.Count - priorCount - 1).Take(priorCount).Count(c => c.Close < vwap.Value);
            if (priorBelow == 0) return null;

            var atr = Atr(candles, 14);
            var distance = latest.Close - vwap.Value;
            var distanceToAtr = atr is > 0m ? distance / atr.Value : distance / vwap.Value;
            var range = latest.High - latest.Low;
            var closePosition = range > 0m ? (latest.Close - latest.Low) / range : 0m;
            var distanceScore = ScoreAtLeast(distanceToAtr, _opts.VwapReclaim.MinDistanceFromVwapToAtr);
            var priorScore = Clamp01((decimal)priorBelow / priorCount);
            var confirmation = Average(distanceScore, ScoreAtLeast(closePosition, _opts.VwapReclaim.MinClosePositionInRange));
            var volume = VolumeScore(candles);
            var final = Clamp01(distanceScore * 0.35m + priorScore * 0.25m + confirmation * 0.25m + volume * 0.15m);

            return Candidate(
                PatternType.VwapReclaim,
                latest,
                final,
                new[] { vwap.Value, latest.Close },
                distanceScore,
                priorScore,
                confirmation,
                volume,
                null,
                new Dictionary<string, string>
                {
                    ["vwap"] = Format(vwap.Value),
                    ["distanceToAtr"] = Format(distanceToAtr),
                    ["priorBelowVwapCandles"] = priorBelow.ToString(CultureInfo.InvariantCulture)
                });
        }

        private static PatternCandidate Candidate(
            PatternType type,
            Candle candle,
            decimal finalQuality,
            IEnumerable<decimal> levels,
            decimal? geometry,
            decimal? context,
            decimal? confirmation,
            decimal? volume,
            decimal? location,
            IDictionary<string, string>? extra = null)
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["geometryScore"] = Format(geometry),
                ["contextScore"] = Format(context),
                ["confirmationScore"] = Format(confirmation),
                ["volumeScore"] = Format(volume),
                ["locationScore"] = Format(location),
                ["finalQuality"] = Format(finalQuality)
            };

            if (extra != null)
            {
                foreach (var pair in extra)
                {
                    metadata[pair.Key] = pair.Value;
                }
            }

            return new PatternCandidate(type, candle.Symbol, candle.Timeframe, candle.TimestampUtc, finalQuality, levels, metadata);
        }

        private static decimal PullbackScore(IReadOnlyList<Candle> candles, int lookback, decimal minPullbackPercent)
        {
            if (candles.Count < 3) return 0m;
            var start = Math.Max(0, candles.Count - Math.Max(2, lookback) - 1);
            var prior = candles.Skip(start).Take(candles.Count - start - 1).ToArray();
            if (prior.Length == 0) return 0m;
            var high = prior.Max(c => c.Close);
            var lastBeforePattern = prior[^1].Close;
            if (high <= 0m) return 0m;
            var pullback = (high - lastBeforePattern) / high;
            return ScoreAtLeast(pullback, minPullbackPercent);
        }

        private static decimal SupportLocationScore(IReadOnlyList<Candle> candles, decimal patternLow, decimal proximityPercent)
        {
            if (candles.Count < 3 || patternLow <= 0m) return 0m;
            var prior = candles.Take(candles.Count - 1).TakeLast(Math.Min(20, candles.Count - 1)).ToArray();
            if (prior.Length == 0) return 0m;
            var recentLow = prior.Min(c => c.Low);
            if (recentLow <= 0m) return 0m;
            var distance = Math.Abs(patternLow - recentLow) / recentLow;
            return ScoreAtMost(distance, proximityPercent);
        }

        private static decimal VolumeScore(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < 6) return 0.5m;
            var latest = candles[^1].Volume;
            var previous = candles.Take(candles.Count - 1).TakeLast(Math.Min(20, candles.Count - 1)).Average(c => c.Volume);
            if (previous <= 0m) return 0.5m;
            return Clamp01(latest / previous / 1.5m);
        }

        private static decimal? Vwap(IReadOnlyList<Candle> candles)
        {
            decimal pv = 0m;
            decimal volume = 0m;
            foreach (var candle in candles)
            {
                var typical = (candle.High + candle.Low + candle.Close) / 3m;
                pv += typical * candle.Volume;
                volume += candle.Volume;
            }

            return volume <= 0m ? null : pv / volume;
        }

        private static decimal? Atr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            var trueRanges = new List<decimal>();
            for (var i = 1; i < candles.Count; i++)
            {
                var current = candles[i];
                var previous = candles[i - 1];
                trueRanges.Add(Math.Max(current.High - current.Low, Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
            }

            if (trueRanges.Count < period) return null;
            return trueRanges.TakeLast(period).Average();
        }

        private static decimal ScoreAtLeast(decimal value, decimal threshold)
        {
            if (threshold <= 0m) return value > 0m ? 1m : 0m;
            return Clamp01(value / threshold);
        }

        private static decimal ScoreAtMost(decimal value, decimal threshold)
        {
            if (threshold <= 0m) return value <= 0m ? 1m : 0m;
            return Clamp01(1m - value / threshold);
        }

        private static decimal ScoreRange(decimal value, decimal min, decimal max)
        {
            if (max <= min) return value >= min ? 1m : 0m;
            if (value < min || value > max) return 0m;
            var midpoint = min + (max - min) / 2m;
            var distance = Math.Abs(value - midpoint);
            return Clamp01(1m - distance / ((max - min) / 2m));
        }

        private static decimal Average(params decimal[] scores)
        {
            return scores.Length == 0 ? 0m : Clamp01(scores.Sum() / scores.Length);
        }

        private static decimal Clamp01(decimal value)
        {
            if (value < 0m) return 0m;
            if (value > 1m) return 1m;
            return value;
        }

        private static string Format(decimal? value)
        {
            return value.HasValue ? Format(value.Value) : string.Empty;
        }

        private static string Format(decimal value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
