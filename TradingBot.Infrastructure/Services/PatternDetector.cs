using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services
{
    public sealed class PatternDetector : IPatternDetector
    {
        private readonly PatternDetectorOptions _options;
        private readonly ILogger<PatternDetector> _logger;
        private readonly Dictionary<PatternDedupKey, DateTime> _lastDetected = new();

        public PatternDetector(PatternDetectorOptions? options, ILogger<PatternDetector> logger, string? detectorVersion = null)
        {
            _options = options ?? new PatternDetectorOptions();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            DetectorVersion = string.IsNullOrWhiteSpace(detectorVersion)
                ? PatternDetectorVersion.Create(_options)
                : detectorVersion.Trim();
        }

        public string DetectorVersion { get; }

        public IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<Candle> candles) => Detect(new PatternDetectionInput
        {
            Candles = candles ?? Array.Empty<Candle>()
        });

        public IReadOnlyList<PatternCandidate> Detect(PatternDetectionInput input) => Process(input).Candidates;

        public PatternDetectionBatch Process(PatternDetectionInput input)
        {
            var candidates = new List<PatternCandidate>();
            var evaluations = Evaluate(input);
            foreach (var evaluation in evaluations)
            {
                var candidate = evaluation.Candidate;
                if (candidate == null || !NotDuplicate(candidate)) continue;
                candidates.Add(candidate);
                _lastDetected[DedupKey(candidate)] = candidate.DetectedAtUtc;
            }
            return new PatternDetectionBatch { Evaluations = evaluations, Candidates = candidates };
        }

        public IReadOnlyList<PatternEvaluation> Evaluate(PatternDetectionInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(input.StrategyId)) throw new ArgumentException("StrategyId is required.", nameof(input));
            if (string.IsNullOrWhiteSpace(input.FeatureVersion)) throw new ArgumentException("FeatureVersion is required.", nameof(input));
            if (input.Directions == null || input.Directions.Count == 0) throw new ArgumentException("At least one trade direction is required.", nameof(input));
            if (input.Directions.Any(direction => !Enum.IsDefined(direction))) throw new ArgumentException("Pattern input contains an unsupported trade direction.", nameof(input));

            var candles = Canonicalize(input.Candles);
            if (candles.Length == 0) return Array.Empty<PatternEvaluation>();
            ValidateSeriesIdentity(candles);

            var evaluations = new List<PatternEvaluation>();
            foreach (var direction in input.Directions.Distinct().OrderByDescending(value => (int)value))
            {
                if (direction == TradeDirection.Long)
                {
                    evaluations.Add(EvaluatePinBar(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.Hammer, _options.Hammer));
                    evaluations.Add(EvaluateEngulfing(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.BullishEngulfing, _options.BullishEngulfing));
                    evaluations.Add(EvaluateDoublePivot(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.DoubleBottom, _options.DoubleBottom));
                    evaluations.Add(EvaluateBreakAndRetest(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.BreakoutAndRetest, _options.BreakoutAndRetest));
                    evaluations.Add(EvaluateVwapCross(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.VwapReclaim, _options.VwapReclaim));
                }
                else
                {
                    evaluations.Add(EvaluatePinBar(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.ShootingStar, _options.ShootingStar));
                    evaluations.Add(EvaluateEngulfing(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.BearishEngulfing, _options.BearishEngulfing));
                    evaluations.Add(EvaluateDoublePivot(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.DoubleTop, _options.DoubleTop));
                    evaluations.Add(EvaluateBreakAndRetest(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.BreakdownAndRetest, _options.BreakdownAndRetest));
                    evaluations.Add(EvaluateVwapCross(candles, input.StrategyId, input.FeatureVersion, direction, PatternType.VwapReject, _options.VwapReject));
                }
            }
            return evaluations;
        }

        private PatternEvaluation EvaluatePinBar(IReadOnlyList<Candle> candles, string strategyId, string featureVersion, TradeDirection direction, PatternType type, HammerPatternOptions options)
        {
            var candle = candles[^1];
            var conditions = new List<PatternConditionResult>
            {
                Condition("minimumCandles", candles.Count >= 2, $"{type} requires at least two completed candles.")
            };
            var range = candle.High - candle.Low;
            conditions.Add(Condition("positiveRange", range > 0m, $"{type} candle must have a positive high-low range."));

            decimal bodyRatio = 0m;
            decimal directionalWickRatio = 0m;
            decimal oppositeWickRatio = 0m;
            decimal directionalClosePosition = 0m;
            if (range > 0m)
            {
                var body = Math.Abs(candle.Close - candle.Open);
                var upperWick = candle.High - Math.Max(candle.Close, candle.Open);
                var lowerWick = Math.Min(candle.Close, candle.Open) - candle.Low;
                bodyRatio = body / range;
                directionalWickRatio = direction == TradeDirection.Long ? lowerWick / range : upperWick / range;
                oppositeWickRatio = direction == TradeDirection.Long ? upperWick / range : lowerWick / range;
                directionalClosePosition = direction == TradeDirection.Long
                    ? (candle.Close - candle.Low) / range
                    : (candle.High - candle.Close) / range;
            }

            conditions.Add(Condition("bodyRatio", bodyRatio <= options.MaxBodyToRange, $"Body/range {Format(bodyRatio)} must be <= {Format(options.MaxBodyToRange)}."));
            conditions.Add(Condition("directionalWick", directionalWickRatio >= options.MinLowerWickToRange, $"Directional wick/range {Format(directionalWickRatio)} must be >= {Format(options.MinLowerWickToRange)}."));
            conditions.Add(Condition("oppositeWick", oppositeWickRatio <= options.MaxUpperWickToRange, $"Opposite wick/range {Format(oppositeWickRatio)} must be <= {Format(options.MaxUpperWickToRange)}."));
            conditions.Add(Condition("closePosition", directionalClosePosition >= options.MinClosePositionInRange, $"Directional close position {Format(directionalClosePosition)} must be >= {Format(options.MinClosePositionInRange)}."));

            var components = new[]
            {
                Component("geometryScore", Average(ScoreAtMost(bodyRatio, options.MaxBodyToRange), ScoreAtLeast(directionalWickRatio, options.MinLowerWickToRange), ScoreAtMost(oppositeWickRatio, options.MaxUpperWickToRange), ScoreAtLeast(directionalClosePosition, options.MinClosePositionInRange)), 0.45m, "Pin-bar body, wick and close geometry."),
                Component("contextScore", DirectionalContextScore(candles, options.PullbackLookbackCandles, options.MinPullbackPercent, direction), 0.20m, "Directional pullback/rally context."),
                Component("confirmationScore", directionalClosePosition, 0.15m, "Close location inside the signal candle."),
                Component("volumeScore", VolumeScore(candles), 0.10m, "Latest volume relative to prior candles."),
                Component("locationScore", DirectionalLocationScore(candles, direction == TradeDirection.Long ? candle.Low : candle.High, options.SupportProximityPercent, direction), 0.10m, "Proximity to recent support/resistance.")
            };
            return BuildEvaluation(type, direction, candle, strategyId, featureVersion, conditions, components,
                new[] { candle.Low, Math.Min(candle.Open, candle.Close), Math.Max(candle.Open, candle.Close), candle.High });
        }

        private PatternEvaluation EvaluateEngulfing(IReadOnlyList<Candle> candles, string strategyId, string featureVersion, TradeDirection direction, PatternType type, BullishEngulfingPatternOptions options)
        {
            var latest = candles[^1];
            var conditions = new List<PatternConditionResult>
            {
                Condition("minimumCandles", candles.Count >= 2, $"{type} requires at least two completed candles.")
            };
            if (candles.Count < 2)
            {
                return BuildEvaluation(type, direction, latest, strategyId, featureVersion, conditions, Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
            }

            var first = candles[^2];
            var second = latest;
            var firstDirectional = direction == TradeDirection.Long ? first.Close < first.Open : first.Close > first.Open;
            var secondDirectional = direction == TradeDirection.Long ? second.Close > second.Open : second.Close < second.Open;
            var engulfed = direction == TradeDirection.Long
                ? second.Open <= first.Close && second.Close >= first.Open
                : second.Open >= first.Close && second.Close <= first.Open;
            var firstBody = Math.Abs(first.Close - first.Open);
            var secondBody = Math.Abs(second.Close - second.Open);
            var bodyRatio = firstBody > 0m ? secondBody / firstBody : 0m;
            var atr = Atr(candles, 14);

            conditions.Add(Condition("firstCandleDirection", firstDirectional, $"First candle must oppose the requested {direction} reversal."));
            conditions.Add(Condition("secondCandleDirection", secondDirectional, $"Second candle must close in the {direction} direction."));
            conditions.Add(Condition("engulfedBody", engulfed, "Second real body must fully engulf the first real body."));
            conditions.Add(Condition("bodyRatio", bodyRatio >= options.MinBodyRatio, $"Body ratio {Format(bodyRatio)} must be >= {Format(options.MinBodyRatio)}."));
            conditions.Add(Condition("firstBodyAtr", !atr.HasValue || atr <= 0m || firstBody / atr.Value >= options.MinFirstBodyToAtr,
                atr.HasValue && atr > 0m ? $"First body/ATR {Format(firstBody / atr.Value)} must be >= {Format(options.MinFirstBodyToAtr)}." : "ATR warm-up unavailable; first-body ATR condition deferred."));
            conditions.Add(Condition("secondBodyAtr", !atr.HasValue || atr <= 0m || secondBody / atr.Value >= options.MinSecondBodyToAtr,
                atr.HasValue && atr > 0m ? $"Second body/ATR {Format(secondBody / atr.Value)} must be >= {Format(options.MinSecondBodyToAtr)}." : "ATR warm-up unavailable; second-body ATR condition deferred."));

            var confirmation = direction == TradeDirection.Long
                ? second.Close > first.High ? 1m : ScoreAtLeast(second.Close, first.Open)
                : second.Close < first.Low ? 1m : ScoreAtLeast(first.Open, second.Close);
            var components = new[]
            {
                Component("geometryScore", Average(ScoreAtLeast(bodyRatio, options.MinBodyRatio), atr is > 0m ? ScoreAtLeast(firstBody / atr.Value, options.MinFirstBodyToAtr) : 0.5m, atr is > 0m ? ScoreAtLeast(secondBody / atr.Value, options.MinSecondBodyToAtr) : 0.5m), 0.55m, "Engulfing-body geometry and ATR-normalized size."),
                Component("contextScore", DirectionalContextScore(candles, options.PullbackLookbackCandles, options.MinPullbackPercent, direction), 0.15m, "Directional pullback/rally context."),
                Component("confirmationScore", confirmation, 0.15m, "Close through the prior candle structure."),
                Component("volumeScore", VolumeScore(candles), 0.10m, "Latest volume relative to prior candles."),
                Component("locationScore", DirectionalLocationScore(candles, direction == TradeDirection.Long ? second.Low : second.High, 0.004m, direction), 0.05m, "Proximity to recent support/resistance.")
            };
            return BuildEvaluation(type, direction, second, strategyId, featureVersion, conditions, components, new[] { first.Low, first.High, second.Low, second.High });
        }

        private PatternEvaluation EvaluateDoublePivot(IReadOnlyList<Candle> candles, string strategyId, string featureVersion, TradeDirection direction, PatternType type, DoubleBottomPatternOptions options)
        {
            var latest = candles[^1];
            var minimum = options.MinSeparation + 2;
            if (candles.Count < minimum)
            {
                return BuildEvaluation(type, direction, latest, strategyId, featureVersion,
                    new[] { Condition("minimumCandles", false, $"{type} requires at least {minimum} candles.") },
                    Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
            }

            PivotMatch? best = null;
            var secondMin = Math.Max(0, candles.Count - options.MaxSeparation - 2);
            for (var secondIndex = candles.Count - 1; secondIndex >= secondMin; secondIndex--)
            {
                for (var firstIndex = secondIndex - options.MaxSeparation; firstIndex <= secondIndex - options.MinSeparation; firstIndex++)
                {
                    if (firstIndex < 0) continue;
                    var firstPrice = direction == TradeDirection.Long ? candles[firstIndex].Low : candles[firstIndex].High;
                    var secondPrice = direction == TradeDirection.Long ? candles[secondIndex].Low : candles[secondIndex].High;
                    if (firstPrice <= 0m || secondPrice <= 0m) continue;
                    var difference = Math.Abs(firstPrice - secondPrice) / Math.Max(firstPrice, secondPrice);
                    if (difference > options.MaxLowDiffPercent) continue;
                    var middle = candles.Skip(firstIndex + 1).Take(secondIndex - firstIndex - 1).ToArray();
                    if (middle.Length == 0) continue;
                    var neckline = direction == TradeDirection.Long ? middle.Max(item => item.High) : middle.Min(item => item.Low);
                    var pivotReference = direction == TradeDirection.Long ? Math.Max(firstPrice, secondPrice) : Math.Min(firstPrice, secondPrice);
                    var rebound = direction == TradeDirection.Long ? (neckline - pivotReference) / pivotReference : (pivotReference - neckline) / pivotReference;
                    if (rebound < options.MinReboundPercent) continue;
                    var afterSecond = candles.Skip(secondIndex + 1).ToArray();
                    var necklineBreak = direction == TradeDirection.Long ? afterSecond.Any(item => item.Close > neckline) : afterSecond.Any(item => item.Close < neckline);
                    var confirmationCandle = direction == TradeDirection.Long
                        ? latest.Close > candles[secondIndex].Close && latest.Close >= neckline * (1m - options.ConfirmationTolerancePercent)
                        : latest.Close < candles[secondIndex].Close && latest.Close <= neckline * (1m + options.ConfirmationTolerancePercent);
                    var confirmation = necklineBreak ? 1m : confirmationCandle ? 0.55m : 0m;
                    var spacing = ScoreRange(secondIndex - firstIndex, options.MinSeparation, options.MaxSeparation);
                    var similarity = ScoreAtMost(difference, options.MaxLowDiffPercent);
                    var volume = VolumeScore(candles);
                    var score = similarity * 0.30m + ScoreAtLeast(rebound, options.MinReboundPercent) * 0.25m + spacing * 0.15m + confirmation * 0.20m + volume * 0.10m;
                    if (best == null || score > best.Score)
                    {
                        best = new PivotMatch(firstPrice, secondPrice, neckline, difference, rebound, spacing, similarity, confirmation, volume, necklineBreak, secondIndex, score);
                    }
                }
            }

            if (best == null)
            {
                return BuildEvaluation(type, direction, latest, strategyId, featureVersion,
                    new[] { Condition("matchingPivots", false, $"No {type} pivot pair satisfied separation, similarity and rebound hard conditions.") },
                    Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
            }

            var conditions = new[]
            {
                Condition("matchingPivots", true, "Two directionally valid pivots were found."),
                Condition("pivotSimilarity", best.Difference <= options.MaxLowDiffPercent, $"Pivot difference {Format(best.Difference)} must be <= {Format(options.MaxLowDiffPercent)}."),
                Condition("minimumRebound", best.Rebound >= options.MinReboundPercent, $"Rebound {Format(best.Rebound)} must be >= {Format(options.MinReboundPercent)}.")
            };
            var components = new[]
            {
                Component("geometryScore", best.Similarity, 0.30m, "Similarity of the two pivot prices."),
                Component("locationScore", ScoreAtLeast(best.Rebound, options.MinReboundPercent), 0.25m, "Move from pivots to neckline."),
                Component("contextScore", best.Spacing, 0.15m, "Candle spacing between pivots."),
                Component("confirmationScore", best.Confirmation, 0.20m, "Directional neckline confirmation."),
                Component("volumeScore", best.Volume, 0.10m, "Latest volume relative to prior candles.")
            };
            var metadata = new Dictionary<string, string>
            {
                ["neckline"] = Format(best.Neckline),
                ["necklineBreak"] = best.NecklineBreak.ToString(CultureInfo.InvariantCulture),
                ["secondPivotTimestampUtc"] = candles[best.SecondIndex].TimestampUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            return BuildEvaluation(type, direction, latest, strategyId, featureVersion, conditions, components,
                new[] { best.FirstPrice, best.SecondPrice, best.Neckline }, metadata);
        }

        private PatternEvaluation EvaluateBreakAndRetest(IReadOnlyList<Candle> candles, string strategyId, string featureVersion, TradeDirection direction, PatternType type, BreakoutAndRetestPatternOptions options)
        {
            var latest = candles[^1];
            var lookback = Math.Max(2, options.Lookback);
            if (candles.Count <= lookback + 2)
            {
                return BuildEvaluation(type, direction, latest, strategyId, featureVersion,
                    new[] { Condition("minimumCandles", false, $"{type} requires more than {lookback + 2} candles.") },
                    Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
            }

            for (var breakIndex = Math.Max(lookback, candles.Count - lookback - 8); breakIndex < candles.Count - 1; breakIndex++)
            {
                var prior = candles.Skip(breakIndex - lookback).Take(lookback).ToArray();
                var level = direction == TradeDirection.Long ? prior.Max(item => item.High) : prior.Min(item => item.Low);
                if (level <= 0m) continue;
                var magnitude = direction == TradeDirection.Long ? (candles[breakIndex].Close - level) / level : (level - candles[breakIndex].Close) / level;
                if (magnitude < options.MinBreakoutPercent) continue;
                for (var retestIndex = breakIndex + 1; retestIndex < candles.Count; retestIndex++)
                {
                    var retestPrice = direction == TradeDirection.Long ? candles[retestIndex].Low : candles[retestIndex].High;
                    var retestDistance = Math.Abs(retestPrice - level) / level;
                    if (retestDistance > options.RetestTolerancePercent) continue;
                    var bounce = direction == TradeDirection.Long ? (latest.Close - level) / level : (level - latest.Close) / level;
                    if (retestIndex == candles.Count - 1 || bounce < options.MinBouncePercent) continue;
                    var range = latest.High - latest.Low;
                    var closePosition = range > 0m ? direction == TradeDirection.Long ? (latest.Close - latest.Low) / range : (latest.High - latest.Close) / range : 0m;
                    var conditions = new[]
                    {
                        Condition("breakMagnitude", true, $"Directional break {Format(magnitude)} met minimum {Format(options.MinBreakoutPercent)}."),
                        Condition("retestDistance", true, $"Retest distance {Format(retestDistance)} met maximum {Format(options.RetestTolerancePercent)}."),
                        Condition("directionalBounce", true, $"Directional bounce {Format(bounce)} met minimum {Format(options.MinBouncePercent)}.")
                    };
                    var components = new[]
                    {
                        Component("geometryScore", ScoreAtLeast(magnitude, options.MinBreakoutPercent), 0.25m, "Magnitude of the initial directional break."),
                        Component("locationScore", ScoreAtMost(retestDistance, options.RetestTolerancePercent), 0.25m, "Retest proximity to the broken level."),
                        Component("confirmationScore", ScoreAtLeast(bounce, options.MinBouncePercent), 0.25m, "Directional bounce after retest."),
                        Component("contextScore", closePosition, 0.15m, "Latest close location in its candle."),
                        Component("volumeScore", VolumeScore(candles), 0.10m, "Latest volume relative to prior candles.")
                    };
                    var metadata = new Dictionary<string, string>
                    {
                        ["breakMagnitude"] = Format(magnitude),
                        ["retestDistance"] = Format(retestDistance),
                        ["bouncePercent"] = Format(bounce)
                    };
                    return BuildEvaluation(type, direction, latest, strategyId, featureVersion, conditions, components,
                        new[] { level, retestPrice, latest.Close }, metadata);
                }
            }
            return BuildEvaluation(type, direction, latest, strategyId, featureVersion,
                new[] { Condition("breakRetestSequence", false, $"No {type} sequence satisfied break, retest and bounce hard conditions.") },
                Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
        }

        private PatternEvaluation EvaluateVwapCross(IReadOnlyList<Candle> candles, string strategyId, string featureVersion, TradeDirection direction, PatternType type, VwapReclaimPatternOptions options)
        {
            var latest = candles[^1];
            var priorCount = Math.Max(1, options.PriorBelowVwapCandles);
            var minimum = priorCount + 1;
            var vwap = Vwap(candles);
            var conditions = new List<PatternConditionResult>
            {
                Condition("minimumCandles", candles.Count >= minimum, $"{type} requires at least {minimum} candles."),
                Condition("validVwap", vwap is > 0m, "VWAP must be available and positive.")
            };
            if (candles.Count < minimum || vwap is not > 0m)
            {
                return BuildEvaluation(type, direction, latest, strategyId, featureVersion, conditions, Array.Empty<PatternScoreComponent>(), Array.Empty<decimal>());
            }

            var crossed = direction == TradeDirection.Long ? latest.Close > vwap.Value : latest.Close < vwap.Value;
            var prior = candles.Skip(candles.Count - priorCount - 1).Take(priorCount).ToArray();
            var priorOpposite = direction == TradeDirection.Long ? prior.Count(item => item.Close < vwap.Value) : prior.Count(item => item.Close > vwap.Value);
            conditions.Add(Condition("directionalCross", crossed, $"Latest close must cross VWAP in the {direction} direction."));
            conditions.Add(Condition("priorOppositeSide", priorOpposite == priorCount, $"All {priorCount} prior closes must be on the opposite side of VWAP; actual {priorOpposite}."));

            var atr = Atr(candles, 14);
            var distance = Math.Abs(latest.Close - vwap.Value);
            var distanceToAtr = atr is > 0m ? distance / atr.Value : distance / vwap.Value;
            var range = latest.High - latest.Low;
            var closePosition = range > 0m ? direction == TradeDirection.Long ? (latest.Close - latest.Low) / range : (latest.High - latest.Close) / range : 0m;
            var distanceScore = ScoreAtLeast(distanceToAtr, options.MinDistanceFromVwapToAtr);
            var priorScore = Clamp01((decimal)priorOpposite / priorCount);
            var confirmation = Average(distanceScore, ScoreAtLeast(closePosition, options.MinClosePositionInRange));
            var components = new[]
            {
                Component("geometryScore", distanceScore, 0.35m, "ATR-normalized distance through VWAP."),
                Component("contextScore", priorScore, 0.25m, "Share of prior closes on the opposite side of VWAP."),
                Component("confirmationScore", confirmation, 0.25m, "VWAP distance and directional close confirmation."),
                Component("volumeScore", VolumeScore(candles), 0.15m, "Latest volume relative to prior candles.")
            };
            var metadata = new Dictionary<string, string>
            {
                ["vwap"] = Format(vwap.Value),
                ["distanceToAtr"] = Format(distanceToAtr),
                ["priorOppositeVwapCandles"] = priorOpposite.ToString(CultureInfo.InvariantCulture)
            };
            return BuildEvaluation(type, direction, latest, strategyId, featureVersion, conditions, components, new[] { vwap.Value, latest.Close }, metadata);
        }

        private PatternEvaluation BuildEvaluation(PatternType type, TradeDirection direction, Candle candle, string strategyId, string featureVersion,
            IEnumerable<PatternConditionResult> conditions, IEnumerable<PatternScoreComponent> components,
            IEnumerable<decimal> levels, IDictionary<string, string>? extraMetadata = null)
        {
            var conditionArray = conditions.ToArray();
            var componentArray = components.ToArray();
            var failedReasons = conditionArray.Where(item => !item.Passed).Select(item => item.Reason).ToArray();
            var finalScore = Clamp01(componentArray.Sum(item => item.WeightedScore));
            PatternCandidate? candidate = null;
            if (failedReasons.Length == 0)
            {
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var component in componentArray) metadata[component.Code] = Format(component.Score);
                metadata["finalQuality"] = Format(finalScore);
                metadata["referencePrice"] = Format(candle.Close);
                metadata["direction"] = direction.ToString();
                metadata["patternVersion"] = DetectorVersion;
                if (extraMetadata != null) foreach (var item in extraMetadata) metadata[item.Key] = item.Value;
                var context = PipelineContext.CreateForSignal(candle.InstrumentId,
                    $"{direction}|{type}|{candle.Timeframe}|{candle.TimestampUtc:O}", strategyId, featureVersion, DetectorVersion);
                candidate = new PatternCandidate(type, candle.Symbol, candle.Timeframe, candle.TimestampUtc, finalScore,
                    levels, metadata, context, direction, conditionArray, componentArray,
                    new[] { $"Accepted {direction} {type}; all hard conditions passed." });
            }

            return new PatternEvaluation
            {
                PatternType = type,
                Direction = direction,
                InstrumentId = candle.InstrumentId,
                Symbol = candle.Symbol,
                StrategyId = strategyId.Trim(),
                FeatureVersion = featureVersion.Trim(),
                PatternVersion = DetectorVersion,
                Timeframe = candle.Timeframe,
                EvaluatedAtUtc = candle.TimestampUtc,
                ReferencePrice = candle.Close,
                HardConditions = conditionArray,
                ScoreComponents = componentArray,
                Reasons = failedReasons.Length == 0 ? new[] { $"Accepted {direction} {type}; all hard conditions passed." } : failedReasons,
                Candidate = candidate
            };
        }

        private bool NotDuplicate(PatternCandidate candidate)
        {
            var key = DedupKey(candidate);
            if (_lastDetected.TryGetValue(key, out var last)
                && candidate.DetectedAtUtc - last < TimeframeDuration(candidate.Timeframe) * Math.Max(1, _options.RecentCandlesForDedup))
            {
                _logger.LogDebug("Duplicate detection suppressed for {InstrumentId} {StrategyId} {Direction} {Timeframe} {Pattern}",
                    candidate.InstrumentId, candidate.StrategyId, candidate.Direction, candidate.Timeframe, candidate.PatternType);
                return false;
            }
            return true;
        }

        private static PatternDedupKey DedupKey(PatternCandidate candidate) => new(
            candidate.InstrumentId.Trim().ToUpperInvariant(), candidate.StrategyId.Trim().ToUpperInvariant(),
            candidate.Direction, candidate.Timeframe, candidate.PatternType);

        private static Candle[] Canonicalize(IReadOnlyList<Candle>? candles) => (candles ?? Array.Empty<Candle>())
            .Where(item => item.IsFinal && item.QualityStatus == MarketDataQualityStatus.Healthy)
            .GroupBy(item => item.TimestampUtc)
            .Select(group => group.OrderBy(item => item.ReceivedTimeUtc).ThenBy(item => item.Source, StringComparer.Ordinal).Last())
            .OrderBy(item => item.TimestampUtc)
            .ToArray();

        private static void ValidateSeriesIdentity(IReadOnlyList<Candle> candles)
        {
            var first = candles[0];
            if (candles.Any(item => !item.InstrumentId.Equals(first.InstrumentId, StringComparison.OrdinalIgnoreCase)
                    || !item.Symbol.Equals(first.Symbol, StringComparison.OrdinalIgnoreCase) || item.Timeframe != first.Timeframe))
            {
                throw new ArgumentException("Pattern input must contain one instrument, symbol and timeframe.", nameof(candles));
            }
        }

        private static decimal DirectionalContextScore(IReadOnlyList<Candle> candles, int lookback, decimal minimumMove, TradeDirection direction)
        {
            if (candles.Count < 3) return 0m;
            var prior = candles.Take(candles.Count - 1).TakeLast(Math.Max(2, lookback)).ToArray();
            if (prior.Length == 0) return 0m;
            decimal move;
            if (direction == TradeDirection.Long)
            {
                var high = prior.Max(item => item.Close);
                move = high > 0m ? (high - prior[^1].Close) / high : 0m;
            }
            else
            {
                var low = prior.Min(item => item.Close);
                move = low > 0m ? (prior[^1].Close - low) / low : 0m;
            }
            return ScoreAtLeast(move, minimumMove);
        }

        private static decimal DirectionalLocationScore(IReadOnlyList<Candle> candles, decimal patternPrice, decimal proximity, TradeDirection direction)
        {
            if (candles.Count < 3 || patternPrice <= 0m) return 0m;
            var prior = candles.Take(candles.Count - 1).TakeLast(Math.Min(20, candles.Count - 1)).ToArray();
            var reference = direction == TradeDirection.Long ? prior.Min(item => item.Low) : prior.Max(item => item.High);
            return reference <= 0m ? 0m : ScoreAtMost(Math.Abs(patternPrice - reference) / reference, proximity);
        }

        private static decimal VolumeScore(IReadOnlyList<Candle> candles)
        {
            if (candles.Count < 6) return 0.5m;
            var previous = candles.Take(candles.Count - 1).TakeLast(Math.Min(20, candles.Count - 1)).Average(item => item.Volume);
            return previous <= 0m ? 0.5m : Clamp01(candles[^1].Volume / previous / 1.5m);
        }

        private static decimal? Vwap(IReadOnlyList<Candle> candles)
        {
            var volume = candles.Sum(item => item.Volume);
            return volume <= 0m ? null : candles.Sum(item => (item.High + item.Low + item.Close) / 3m * item.Volume) / volume;
        }

        private static decimal? Atr(IReadOnlyList<Candle> candles, int period)
        {
            if (candles.Count <= period) return null;
            var ranges = new List<decimal>(candles.Count - 1);
            for (var index = 1; index < candles.Count; index++)
            {
                var current = candles[index];
                var previous = candles[index - 1];
                ranges.Add(Math.Max(current.High - current.Low, Math.Max(Math.Abs(current.High - previous.Close), Math.Abs(current.Low - previous.Close))));
            }
            return ranges.TakeLast(period).Average();
        }

        private static PatternConditionResult Condition(string code, bool passed, string reason) => new(code, passed, reason);
        private static PatternScoreComponent Component(string code, decimal score, decimal weight, string reason) => new(code, Clamp01(score), weight, reason);
        private static decimal ScoreAtLeast(decimal value, decimal threshold) => threshold <= 0m ? value > 0m ? 1m : 0m : Clamp01(value / threshold);
        private static decimal ScoreAtMost(decimal value, decimal threshold) => threshold <= 0m ? value <= 0m ? 1m : 0m : Clamp01(1m - value / threshold);
        private static decimal ScoreRange(decimal value, decimal minimum, decimal maximum)
        {
            if (maximum <= minimum) return value >= minimum ? 1m : 0m;
            if (value < minimum || value > maximum) return 0m;
            var midpoint = minimum + (maximum - minimum) / 2m;
            return Clamp01(1m - Math.Abs(value - midpoint) / ((maximum - minimum) / 2m));
        }
        private static decimal Average(params decimal[] values) => values.Length == 0 ? 0m : Clamp01(values.Average());
        private static decimal Clamp01(decimal value) => Math.Clamp(value, 0m, 1m);
        private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
        private static TimeSpan TimeframeDuration(Timeframe timeframe) => timeframe switch
        {
            Timeframe.OneMinute => TimeSpan.FromMinutes(1),
            Timeframe.FiveMinutes => TimeSpan.FromMinutes(5),
            Timeframe.FifteenMinutes => TimeSpan.FromMinutes(15),
            Timeframe.OneHour => TimeSpan.FromHours(1),
            Timeframe.FourHour => TimeSpan.FromHours(4),
            Timeframe.Daily => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(1)
        };

        private readonly record struct PatternDedupKey(string InstrumentId, string StrategyId, TradeDirection Direction, Timeframe Timeframe, PatternType PatternType);
        private sealed record PivotMatch(decimal FirstPrice, decimal SecondPrice, decimal Neckline, decimal Difference, decimal Rebound,
            decimal Spacing, decimal Similarity, decimal Confirmation, decimal Volume, bool NecklineBreak, int SecondIndex, decimal Score);
    }
}
