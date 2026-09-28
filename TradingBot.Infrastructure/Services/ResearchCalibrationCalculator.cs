using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ResearchCalibrationCalculator
    {
        private const decimal ProbabilityEpsilon = 0.000001m;
        private readonly ResearchCalibrationSettings _settings;
        private readonly ResearchEvaluationCalculator _evaluationCalculator;

        public ResearchCalibrationCalculator(ResearchCalibrationSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _evaluationCalculator = new ResearchEvaluationCalculator(new ResearchEvaluationSettings());
        }

        public ResearchCalibrationCalculation Calculate(
            IReadOnlyCollection<ResearchEvaluationObservation> source,
            ResearchEvaluationRunSnapshot evaluation)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(evaluation);
            var eligible = source
                .Where(item => item.Outcome != ResearchCandidateOutcome.Rejected)
                .OrderBy(item => item.EvaluatedAtUtc)
                .ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
                .ToArray();
            var preHoldout = eligible
                .Where(item => item.EvaluatedAtUtc >= evaluation.FromUtc && item.LabelWindowEndUtc <= evaluation.HoldoutStartUtc)
                .ToArray();
            var holdout = eligible
                .Where(item => item.EvaluatedAtUtc >= evaluation.HoldoutStartUtc && item.LabelWindowEndUtc <= evaluation.ToUtc)
                .ToArray();
            if (preHoldout.Length < _settings.MinimumCalibrationSamples)
            {
                throw new InvalidOperationException($"Calibration requires at least {_settings.MinimumCalibrationSamples} pre-holdout samples; found {preHoldout.Length}.");
            }
            if (holdout.Length == 0) throw new InvalidOperationException("Calibration requires the evaluation's untouched holdout observations.");

            var stability = CalculateThresholdStability(evaluation.Folds);
            var stableThreshold = stability.MedianThreshold;
            var finalPoints = Fit(preHoldout);
            var minimumProbability = Predict(finalPoints, stableThreshold);
            var walkForwardPredictions = new List<Prediction>();
            foreach (var fold in evaluation.Folds.Where(item => item.SelectedConfidenceThreshold.HasValue))
            {
                var training = eligible
                    .Where(item => item.EvaluatedAtUtc >= fold.TrainingFromUtc && item.LabelWindowEndUtc <= fold.TrainingToUtc)
                    .ToArray();
                var validation = eligible
                    .Where(item => item.EvaluatedAtUtc >= fold.ValidationFromUtc && item.LabelWindowEndUtc <= fold.ValidationToUtc)
                    .ToArray();
                if (training.Length < _settings.MinimumCalibrationSamples || validation.Length == 0) continue;
                var foldPoints = Fit(training);
                walkForwardPredictions.AddRange(validation.Select(item => new Prediction(item, Predict(foldPoints, item.Confidence))));
            }
            if (walkForwardPredictions.Count == 0)
            {
                throw new InvalidOperationException("No walk-forward fold contains enough observations to evaluate probability calibration out of sample.");
            }

            var holdoutPredictions = holdout.Select(item => new Prediction(item, Predict(finalPoints, item.Confidence))).ToArray();
            var proposedHoldout = holdout.Where(item => item.Confidence >= stableThreshold).ToArray();
            var proposedMetrics = _evaluationCalculator.CalculateMetrics(proposedHoldout);
            var walkRaw = CalculateMetrics(walkForwardPredictions.Select(item => new Prediction(item.Observation, item.Observation.Confidence)));
            var walkCalibrated = CalculateMetrics(walkForwardPredictions);
            var holdoutRaw = CalculateMetrics(holdoutPredictions.Select(item => new Prediction(item.Observation, item.Observation.Confidence)));
            var holdoutCalibrated = CalculateMetrics(holdoutPredictions);
            var comparison = CompareWithBaseline(
                evaluation.SelectedConfidenceThreshold,
                stableThreshold,
                evaluation.HoldoutMetrics,
                proposedMetrics,
                walkRaw,
                walkCalibrated,
                holdoutRaw,
                holdoutCalibrated,
                stability);
            var wins = preHoldout.Where(item => item.NetReturnBps > 0m).Select(item => item.NetReturnBps).ToArray();
            var losses = preHoldout.Where(item => item.NetReturnBps <= 0m).Select(item => item.NetReturnBps).ToArray();

            return new ResearchCalibrationCalculation
            {
                CalibrationPoints = finalPoints,
                ThresholdStability = stability,
                StableConfidenceThreshold = stableThreshold,
                MinimumCalibratedProbability = minimumProbability,
                AverageWinBps = wins.Length == 0 ? 0m : wins.Average(),
                AverageLossBps = losses.Length == 0 ? 0m : losses.Average(),
                AverageEstimatedCostBps = preHoldout.Average(item => item.EstimatedCostBps),
                WalkForwardRawMetrics = walkRaw,
                WalkForwardCalibratedMetrics = walkCalibrated,
                HoldoutRawMetrics = holdoutRaw,
                HoldoutCalibratedMetrics = holdoutCalibrated,
                ProposedHoldoutMetrics = proposedMetrics,
                BaselineComparison = comparison
            };
        }

        public IReadOnlyList<ProbabilityCalibrationPoint> Fit(IReadOnlyCollection<ResearchEvaluationObservation> source)
        {
            if (source.Count < _settings.MinimumCalibrationSamples)
            {
                throw new InvalidOperationException($"Calibration fit requires at least {_settings.MinimumCalibrationSamples} samples.");
            }
            var confidenceGroups = source
                .OrderBy(item => item.Confidence)
                .ThenBy(item => item.EvaluatedAtUtc)
                .ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
                .GroupBy(item => item.Confidence)
                .Select(group => Block.From(group))
                .ToArray();
            var seeded = new List<Block>();
            Block? pending = null;
            foreach (var group in confidenceGroups)
            {
                pending = pending == null ? group : Block.Merge(pending, group);
                if (pending.SampleCount >= _settings.MinimumCalibrationBinSize)
                {
                    seeded.Add(pending);
                    pending = null;
                }
            }
            if (pending != null)
            {
                if (seeded.Count == 0) seeded.Add(pending);
                else seeded[^1] = Block.Merge(seeded[^1], pending);
            }

            var monotonic = new List<Block>();
            foreach (var block in seeded)
            {
                monotonic.Add(block);
                while (monotonic.Count >= 2 && monotonic[^2].WinRate > monotonic[^1].WinRate)
                {
                    var merged = Block.Merge(monotonic[^2], monotonic[^1]);
                    monotonic.RemoveRange(monotonic.Count - 2, 2);
                    monotonic.Add(merged);
                }
            }
            return monotonic.Select(item => new ProbabilityCalibrationPoint(
                item.MinimumConfidence,
                item.MaximumConfidence,
                item.WinRate,
                item.SampleCount,
                item.WinRate,
                item.NetReturnSum / item.SampleCount)).ToArray();
        }

        public decimal Predict(IReadOnlyList<ProbabilityCalibrationPoint> points, decimal confidence)
        {
            if (points.Count == 0) throw new InvalidOperationException("Calibration map is empty.");
            var normalized = Math.Clamp(confidence, 0m, 1m);
            return points.FirstOrDefault(item => normalized <= item.MaximumConfidence)?.CalibratedProbability
                ?? points[^1].CalibratedProbability;
        }

        public ProbabilityCalibrationMetrics CalculateMetrics(IEnumerable<(decimal Probability, bool Won)> source) =>
            CalculateMetrics(source.Select((item, index) => new MetricPrediction(index, item.Probability, item.Won)));

        private ProbabilityCalibrationMetrics CalculateMetrics(IEnumerable<Prediction> source) => CalculateMetrics(
            source.Select(item => new MetricPrediction(item.Observation.CandidateId, item.Probability, item.Observation.NetReturnBps > 0m)));

        private ProbabilityCalibrationMetrics CalculateMetrics(IEnumerable<MetricPrediction> source)
        {
            var values = source.ToArray();
            if (values.Length == 0) return ResearchCalibrationCalculation.EmptyCalibrationMetrics;
            var brier = values.Average(item => Square(item.Probability - (item.Won ? 1m : 0m)));
            var logLoss = values.Average(item =>
            {
                var probability = Math.Clamp(item.Probability, ProbabilityEpsilon, 1m - ProbabilityEpsilon);
                return Convert.ToDecimal(-(item.Won ? Math.Log((double)probability) : Math.Log((double)(1m - probability))));
            });
            var ece = values
                .GroupBy(item => Math.Min(_settings.CalibrationMetricBins - 1, (int)Math.Floor(item.Probability * _settings.CalibrationMetricBins)))
                .Sum(group => Math.Abs(group.Average(item => item.Probability) - DecimalRatio(group.Count(item => item.Won), group.Count()))
                    * DecimalRatio(group.Count(), values.Length));
            return new ProbabilityCalibrationMetrics(
                values.Length,
                brier,
                logLoss,
                ece,
                values.Average(item => item.Probability),
                DecimalRatio(values.Count(item => item.Won), values.Length));
        }

        private ThresholdStabilityResult CalculateThresholdStability(IReadOnlyCollection<WalkForwardFoldResult> folds)
        {
            var thresholds = folds.Where(item => item.SelectedConfidenceThreshold.HasValue)
                .Select(item => item.SelectedConfidenceThreshold!.Value)
                .OrderBy(value => value)
                .ToArray();
            if (thresholds.Length == 0) return new ThresholdStabilityResult(0, 0m, 0m, 0m, 0m, 0m, false, "No completed walk-forward thresholds exist.");
            var median = thresholds.Length % 2 == 1
                ? thresholds[thresholds.Length / 2]
                : (thresholds[thresholds.Length / 2 - 1] + thresholds[thresholds.Length / 2]) / 2m;
            var minimum = thresholds[0];
            var maximum = thresholds[^1];
            var range = maximum - minimum;
            var agreement = DecimalRatio(thresholds.Count(value => Math.Abs(value - median) <= _settings.StableThresholdTolerance), thresholds.Length);
            var reasons = new List<string>();
            if (thresholds.Length < _settings.MinimumCalibrationFoldCount) reasons.Add($"requires at least {_settings.MinimumCalibrationFoldCount} folds");
            if (range > _settings.MaximumStableThresholdRange) reasons.Add($"threshold range {range} exceeds {_settings.MaximumStableThresholdRange}");
            if (agreement < _settings.MinimumFoldAgreementRatio) reasons.Add($"fold agreement {agreement} is below {_settings.MinimumFoldAgreementRatio}");
            var stable = reasons.Count == 0;
            return new ThresholdStabilityResult(
                thresholds.Length, minimum, maximum, median, range, agreement, stable,
                stable ? "Walk-forward threshold is stable under the configured fold, range and agreement rules." : $"Threshold is unstable: {string.Join("; ", reasons)}.");
        }

        private CalibrationBaselineComparison CompareWithBaseline(
            decimal baselineThreshold,
            decimal proposedThreshold,
            EvaluationMetrics baseline,
            EvaluationMetrics proposed,
            ProbabilityCalibrationMetrics walkRaw,
            ProbabilityCalibrationMetrics walkCalibrated,
            ProbabilityCalibrationMetrics holdoutRaw,
            ProbabilityCalibrationMetrics holdoutCalibrated,
            ThresholdStabilityResult stability)
        {
            var sampleRatio = baseline.SampleCount == 0 ? 0m : DecimalRatio(proposed.SampleCount, baseline.SampleCount);
            var expectancyDelta = proposed.ExpectancyBps - baseline.ExpectancyBps;
            var drawdownDelta = proposed.MaximumDrawdownBps - baseline.MaximumDrawdownBps;
            var oosBrierDelta = walkCalibrated.BrierScore - walkRaw.BrierScore;
            var holdoutBrierDelta = holdoutCalibrated.BrierScore - holdoutRaw.BrierScore;
            var reasons = new List<string>();
            if (!stability.IsStable) reasons.Add(stability.Reason);
            if (oosBrierDelta > _settings.MaximumOutOfSampleBrierDegradation)
            {
                reasons.Add($"Out-of-sample calibrated Brier score degrades raw confidence by {oosBrierDelta}.");
            }
            if (expectancyDelta < -_settings.MaximumHoldoutExpectancyDegradationBps)
            {
                reasons.Add($"Holdout expectancy delta {expectancyDelta} bps is below the allowed degradation.");
            }
            if (drawdownDelta > _settings.MaximumHoldoutDrawdownIncreaseBps)
            {
                reasons.Add($"Holdout drawdown increases by {drawdownDelta} bps.");
            }
            if (sampleRatio < _settings.MinimumHoldoutSampleRatio)
            {
                reasons.Add($"Proposed threshold retains only {sampleRatio:P1} of baseline holdout samples.");
            }
            return new CalibrationBaselineComparison(
                baselineThreshold,
                proposedThreshold,
                baseline,
                proposed,
                expectancyDelta,
                drawdownDelta,
                sampleRatio,
                oosBrierDelta,
                holdoutBrierDelta,
                reasons.Count == 0,
                reasons);
        }

        private static decimal Square(decimal value) => value * value;
        private static decimal DecimalRatio(int numerator, int denominator) => denominator == 0 ? 0m : (decimal)numerator / denominator;

        private sealed record Prediction(ResearchEvaluationObservation Observation, decimal Probability);
        private sealed record MetricPrediction(long Id, decimal Probability, bool Won);

        private sealed record Block(
            decimal MinimumConfidence,
            decimal MaximumConfidence,
            int SampleCount,
            int WinCount,
            decimal NetReturnSum)
        {
            public decimal WinRate => DecimalRatio(WinCount, SampleCount);

            public static Block From(IEnumerable<ResearchEvaluationObservation> source)
            {
                var values = source.ToArray();
                return new Block(
                    values.Min(item => item.Confidence),
                    values.Max(item => item.Confidence),
                    values.Length,
                    values.Count(item => item.NetReturnBps > 0m),
                    values.Sum(item => item.NetReturnBps));
            }

            public static Block Merge(Block left, Block right) => new(
                Math.Min(left.MinimumConfidence, right.MinimumConfidence),
                Math.Max(left.MaximumConfidence, right.MaximumConfidence),
                left.SampleCount + right.SampleCount,
                left.WinCount + right.WinCount,
                left.NetReturnSum + right.NetReturnSum);
        }
    }
}
