using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class ResearchEvaluationCalculatorTests
    {
        private static readonly DateTime StartUtc = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void ThresholdIsSelectedBeforeHoldoutAndDoesNotOptimizeWinRate()
        {
            var settings = Settings();
            var calculator = new ResearchEvaluationCalculator(settings);
            var baseline = BuildThirtyDays();

            var first = calculator.Calculate(baseline, StartUtc, StartUtc.AddDays(30));
            var modifiedHoldout = baseline.Select(item => item.EvaluatedAtUtc >= StartUtc.AddDays(25)
                    ? item with { GrossReturnBps = 1_000m, NetReturnBps = 999m }
                    : item)
                .ToArray();
            var second = calculator.Calculate(modifiedHoldout, StartUtc, StartUtc.AddDays(30));

            Assert.Equal(0.8m, first.SelectedConfidenceThreshold);
            Assert.Equal(first.SelectedConfidenceThreshold, second.SelectedConfidenceThreshold);
            Assert.Contains("without using win rate", first.SelectionReason, StringComparison.OrdinalIgnoreCase);
            Assert.True(first.PreHoldoutThresholds.Single(item => item.ConfidenceThreshold == 0m).Metrics.WinRate
                > first.PreHoldoutThresholds.Single(item => item.ConfidenceThreshold == 0.8m).Metrics.WinRate);
            Assert.NotEqual(first.HoldoutMetrics.NetProfitBps, second.HoldoutMetrics.NetProfitBps);
        }

        [Fact]
        public void MetricsIncludeExpectancyProfitFactorAndChronologicalDrawdown()
        {
            var calculator = new ResearchEvaluationCalculator(Settings());
            var rows = new[]
            {
                Observation(1, StartUtc, 0.9m, 10m),
                Observation(2, StartUtc.AddMinutes(1), 0.9m, -5m),
                Observation(3, StartUtc.AddMinutes(2), 0.9m, -10m)
            };

            var metrics = calculator.CalculateMetrics(rows);

            Assert.Equal(3, metrics.SampleCount);
            Assert.Equal(1, metrics.WinCount);
            Assert.Equal(-5m / 3m, metrics.ExpectancyBps);
            Assert.Equal(10m / 15m, metrics.ProfitFactor);
            Assert.Equal(15m, metrics.MaximumDrawdownBps);
        }

        [Fact]
        public void RejectedCandidatesAreReportedButNeverEligibleForThresholds()
        {
            var source = BuildThirtyDays().ToList();
            source.AddRange(Enumerable.Range(10_000, 20).Select(index => Observation(
                index,
                StartUtc.AddDays(index % 20),
                1m,
                5_000m,
                ResearchCandidateOutcome.Rejected)));

            var result = new ResearchEvaluationCalculator(Settings()).Calculate(source, StartUtc, StartUtc.AddDays(30));

            Assert.Equal(source.Count, result.SourceCandidateCount);
            Assert.Equal(source.Count - 20, result.EligibleCandidateCount);
            Assert.DoesNotContain(result.HoldoutCandidateIds, id => id >= 10_000);
        }

        [Fact]
        public void MixedPipelineVersionsFailClosed()
        {
            var source = BuildThirtyDays().ToArray();
            source[0] = source[0] with { LabelVersion = "labels-other" };

            var error = Assert.Throws<InvalidOperationException>(() =>
                new ResearchEvaluationCalculator(Settings()).Calculate(source, StartUtc, StartUtc.AddDays(30)));

            Assert.Contains("exactly one label version", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ProducesCostStressAndAllRequiredSegmentDimensions()
        {
            var result = new ResearchEvaluationCalculator(Settings()).Calculate(BuildThirtyDays(), StartUtc, StartUtc.AddDays(30));

            Assert.Equal(new[] { 1m, 2m }, result.CostSensitivity.Select(item => item.CostMultiplier));
            Assert.True(result.CostSensitivity[1].Metrics.ExpectancyBps < result.CostSensitivity[0].Metrics.ExpectancyBps);
            foreach (var dimension in new[] { "Instrument", "Strategy", "Pattern", "Direction", "Timeframe", "UtcWeekday", "NewYorkSession", "MarketRegime", "Liquidity" })
            {
                Assert.Contains(result.Segments, item => item.Dimension == dimension);
            }
            Assert.All(result.Folds.Where(item => item.SelectedConfidenceThreshold.HasValue), fold =>
                Assert.True(fold.TrainingToUtc <= fold.ValidationFromUtc));
        }

        private static ResearchEvaluationSettings Settings() => new()
        {
            TrainingWindowDays = 10,
            ValidationWindowDays = 5,
            StepDays = 5,
            HoldoutDays = 5,
            MinimumTrainingSamples = 20,
            MinimumValidationSamples = 10,
            MinimumHoldoutSamples = 10,
            ConfidenceThresholds = new[] { 0m, 0.8m },
            CostStressMultipliers = new[] { 1m, 2m }
        };

        private static IReadOnlyList<ResearchEvaluationObservation> BuildThirtyDays()
        {
            var result = new List<ResearchEvaluationObservation>();
            long id = 1;
            for (var day = 0; day < 30; day++)
            {
                var time = StartUtc.AddDays(day);
                result.Add(Observation(id++, time, 0.9m, day % 2 == 0 ? 10m : -5m));
                result.Add(Observation(id++, time.AddMinutes(1), 0.9m, day % 2 == 0 ? -5m : 10m));
                result.Add(Observation(id++, time.AddMinutes(2), 0.5m, 1m));
                result.Add(Observation(id++, time.AddMinutes(3), 0.5m, day % 4 == 0 ? -20m : 1m));
            }
            return result;
        }

        private static ResearchEvaluationObservation Observation(
            long id,
            DateTime time,
            decimal confidence,
            decimal net,
            ResearchCandidateOutcome outcome = ResearchCandidateOutcome.Accepted) => new(
                id,
                $"candidate-{id}",
                "US-STK-SPY-SMART",
                "micro",
                PatternType.Hammer,
                TradeDirection.Long,
                Timeframe.OneMinute,
                time,
                time.AddMinutes(1),
                confidence,
                outcome,
                MarketRegime.Trending.ToString(),
                id % 2 == 0 ? 0.5m : 1.5m,
                net + 2m,
                2m,
                net,
                12m,
                8m,
                net > 0m ? TargetStopOutcome.TargetFirst : TargetStopOutcome.StopFirst,
                "features-test",
                "patterns-test",
                "labels-test");
    }
}
