using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class ResearchCalibrationCalculatorTests
    {
        private static readonly DateTime StartUtc = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void IsotonicCalibrationIsMonotonicAndReportsProbabilityMetrics()
        {
            var calculator = new ResearchCalibrationCalculator(Settings());
            var source = BuildFortyDays();

            var points = calculator.Fit(source.Where(item => item.EvaluatedAtUtc < StartUtc.AddDays(35)).ToArray());
            var metrics = calculator.CalculateMetrics(new[]
            {
                (Probability: 0.8m, Won: true),
                (Probability: 0.2m, Won: false),
                (Probability: 0.7m, Won: true),
                (Probability: 0.3m, Won: false)
            });

            Assert.NotEmpty(points);
            Assert.All(points.Zip(points.Skip(1)), pair =>
                Assert.True(pair.First.CalibratedProbability <= pair.Second.CalibratedProbability));
            Assert.Equal(4, metrics.SampleCount);
            Assert.Equal(0.065m, metrics.BrierScore);
            Assert.Equal(0.5m, metrics.ObservedWinRate);
        }

        [Fact]
        public void HoldoutChangesCannotAlterCalibrationMapOrStableThreshold()
        {
            var calculator = new ResearchCalibrationCalculator(Settings());
            var source = BuildFortyDays();
            var evaluation = Evaluation();

            var first = calculator.Calculate(source, evaluation);
            var changedHoldout = source.Select(item => item.EvaluatedAtUtc >= evaluation.HoldoutStartUtc
                    ? item with { GrossReturnBps = 1_002m, NetReturnBps = 1_000m }
                    : item)
                .ToArray();
            var second = calculator.Calculate(changedHoldout, evaluation);

            Assert.Equal(first.CalibrationPoints, second.CalibrationPoints);
            Assert.Equal(first.StableConfidenceThreshold, second.StableConfidenceThreshold);
            Assert.Equal(first.WalkForwardCalibratedMetrics, second.WalkForwardCalibratedMetrics);
            Assert.NotEqual(first.HoldoutCalibratedMetrics, second.HoldoutCalibratedMetrics);
        }

        [Fact]
        public void UnstableFoldThresholdsFailApprovalGate()
        {
            var settings = Settings();
            settings.MaximumStableThresholdRange = 0.10m;
            settings.StableThresholdTolerance = 0.01m;
            settings.MinimumFoldAgreementRatio = 0.75m;
            var evaluation = Evaluation(new[] { 0.1m, 0.4m, 0.8m, 0.9m });

            var result = new ResearchCalibrationCalculator(settings).Calculate(BuildFortyDays(), evaluation);

            Assert.False(result.ThresholdStability.IsStable);
            Assert.False(result.BaselineComparison.PassesApprovalGate);
            Assert.Contains(result.BaselineComparison.Reasons, reason => reason.Contains("unstable", StringComparison.OrdinalIgnoreCase));
        }

        private static ResearchCalibrationSettings Settings() => new()
        {
            MinimumCalibrationSamples = 20,
            MinimumCalibrationFoldCount = 2,
            MinimumCalibrationBinSize = 5,
            CalibrationMetricBins = 5,
            MaximumStableThresholdRange = 0.4m,
            StableThresholdTolerance = 0.2m,
            MinimumFoldAgreementRatio = 0.5m,
            MaximumOutOfSampleBrierDegradation = 1m,
            MaximumHoldoutExpectancyDegradationBps = 1_000m,
            MaximumHoldoutDrawdownIncreaseBps = 10_000m,
            MinimumHoldoutSampleRatio = 0.1m
        };

        private static ResearchEvaluationRunSnapshot Evaluation(decimal[]? thresholds = null)
        {
            thresholds ??= new[] { 0.5m, 0.6m, 0.5m, 0.6m };
            var folds = thresholds.Select((threshold, index) => new WalkForwardFoldResult(
                index + 1,
                StartUtc.AddDays(index * 5),
                StartUtc.AddDays(10 + index * 5),
                StartUtc.AddDays(10 + index * 5),
                StartUtc.AddDays(15 + index * 5),
                50,
                25,
                threshold,
                "test",
                ResearchEvaluationCalculation.EmptyMetrics,
                ResearchEvaluationCalculation.EmptyMetrics)).ToArray();
            var baseline = new EvaluationMetrics(20, 12, 8, 0, 0.6m, 2m, 40m, 80m, 40m, 2m, 20m, 10m, 8m, 0.6m, 0.4m, 0m);
            return new ResearchEvaluationRunSnapshot(
                Guid.NewGuid(), "run", null, string.Empty, string.Empty, 60,
                StartUtc, StartUtc.AddDays(40), StartUtc.AddDays(35), 200, 200, folds.Length,
                0.5m, "baseline", "evaluation-test", "features-test", "patterns-test", "labels-test",
                "input", "output", baseline, baseline, baseline,
                Array.Empty<ThresholdEvaluation>(), folds, Array.Empty<CostSensitivityResult>(), Array.Empty<EvaluationSegmentResult>(),
                StartUtc, StartUtc.AddDays(40));
        }

        private static IReadOnlyList<ResearchEvaluationObservation> BuildFortyDays()
        {
            var result = new List<ResearchEvaluationObservation>();
            long id = 1;
            for (var day = 0; day < 40; day++)
            {
                for (var sample = 0; sample < 5; sample++)
                {
                    var confidence = 0.2m + sample * 0.18m;
                    var won = sample >= 3 || (sample == 2 && day % 2 == 0) || (sample == 1 && day % 5 == 0);
                    var net = won ? 10m : -6m;
                    var time = StartUtc.AddDays(day).AddMinutes(sample);
                    result.Add(new ResearchEvaluationObservation(
                        id,
                        $"candidate-{id}",
                        day % 2 == 0 ? "SPY" : "QQQ",
                        "micro",
                        PatternType.Hammer,
                        TradeDirection.Long,
                        Timeframe.OneMinute,
                        time,
                        time.AddMinutes(1),
                        confidence,
                        ResearchCandidateOutcome.Accepted,
                        MarketRegime.Trending.ToString(),
                        1m,
                        net + 2m,
                        2m,
                        net,
                        12m,
                        8m,
                        won ? TargetStopOutcome.TargetFirst : TargetStopOutcome.StopFirst,
                        "features-test",
                        "patterns-test",
                        "labels-test"));
                    id++;
                }
            }
            return result;
        }
    }
}
