using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests
{
    public sealed class CandidateLabelCalculatorTests
    {
        private static readonly DateTime StartUtc = new(2026, 9, 25, 14, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void LongLabelUsesOnlyStrictlyFutureObservationsAndSubtractsRealisticCost()
        {
            var calculator = CreateCalculator();
            var result = calculator.Calculate(
                TradeDirection.Long,
                100m,
                StartUtc,
                StartUtc.AddSeconds(15),
                StartUtc.AddSeconds(15),
                new[]
                {
                    new CandidatePriceObservation(StartUtc, 200m, 1m, 200m, "same-time"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.15m, 99.95m, 100.10m, "trade-1"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(10), 100.17m, 100.07m, 100.11m, "trade-mid"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(15), 100.20m, 100.05m, 100.12m, "trade-2"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(16), 300m, 1m, 300m, "future")
                },
                new[] { new CandidateSpreadObservation(StartUtc.AddSeconds(1), 99.99m, 100.01m) });

            Assert.Equal(CandidateLabelStatus.Complete, result.Status);
            Assert.Equal(TargetStopOutcome.TargetFirst, result.TargetStopOutcome);
            Assert.Equal(3, result.ObservationCount);
            Assert.Equal(15m, result.MaximumFavorableExcursionBps);
            Assert.Equal(5m, result.MaximumAdverseExcursionBps);
            Assert.Equal(12m, result.GrossReturnBps);
            Assert.Equal(12m, result.HorizonGrossReturnBps);
            Assert.True(result.EstimatedCostBps > 4.49m && result.EstimatedCostBps < 4.51m);
            Assert.True(result.NetReturnBps > 7.49m && result.NetReturnBps < 7.51m);
        }

        [Fact]
        public void ShortLabelIsDirectionAwareAndCanHitStopFirst()
        {
            var result = CreateCalculator().Calculate(
                TradeDirection.Short,
                100m,
                StartUtc,
                StartUtc.AddSeconds(5),
                StartUtc.AddSeconds(5),
                new[] { new CandidatePriceObservation(StartUtc.AddSeconds(1), 100.10m, 99.98m, 100.05m, "tick") });

            Assert.Equal(TargetStopOutcome.StopFirst, result.TargetStopOutcome);
            Assert.Equal(10m, result.MaximumAdverseExcursionBps);
            Assert.Equal(2m, result.MaximumFavorableExcursionBps);
            Assert.Equal(-5m, result.GrossReturnBps);
        }

        [Fact]
        public void SameBarTargetAndStopTouchIsMarkedAmbiguous()
        {
            var result = CreateCalculator().Calculate(
                TradeDirection.Long,
                100m,
                StartUtc,
                StartUtc.AddMinutes(1),
                StartUtc.AddMinutes(1),
                new[] { new CandidatePriceObservation(StartUtc.AddMinutes(1), 100.20m, 99.85m, 100.05m, "bar", true) });

            Assert.Equal(CandidateLabelStatus.Ambiguous, result.Status);
            Assert.Equal(TargetStopOutcome.Ambiguous, result.TargetStopOutcome);
            Assert.Contains(result.Reasons, reason => reason.Contains("intrabar", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void IncompleteHorizonRemainsPending()
        {
            var result = CreateCalculator().Calculate(
                TradeDirection.Long,
                100m,
                StartUtc,
                StartUtc.AddMinutes(5),
                StartUtc.AddMinutes(4),
                Array.Empty<CandidatePriceObservation>());

            Assert.Equal(CandidateLabelStatus.Pending, result.Status);
            Assert.Null(result.NetReturnBps);
        }

        [Fact]
        public void SparseTicksCannotProduceSecondScaleLabel()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(15), StartUtc.AddSeconds(15),
                new[]
                {
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 100.05m, 100.05m, 100.05m, "tick-1"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(15), 100.40m, 100.40m, 100.40m, "tick-15")
                });

            Assert.Equal(CandidateLabelStatus.InsufficientData, result.Status);
            Assert.Null(result.NetReturnBps);
            Assert.Contains(result.Reasons, reason => reason.Contains("gap", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void MinuteBarWithoutTicksCannotProduceFiveSecondLabel()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5),
                new[] { new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.05m, 99.95m, 100m, "bar", true) });

            Assert.Equal(CandidateLabelStatus.InsufficientData, result.Status);
            Assert.Null(result.GrossReturnBps);
        }

        [Fact]
        public void StopExitAndHorizonReturnAreDistinct()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5),
                new[]
                {
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 99.90m, 99.90m, 99.90m, "stop"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.50m, 100.50m, 100.50m, "horizon")
                });

            Assert.Equal(CandidateLabelStatus.Complete, result.Status);
            Assert.Equal(TargetStopOutcome.StopFirst, result.TargetStopOutcome);
            Assert.Equal(-10m, result.GrossReturnBps);
            Assert.Equal(50m, result.HorizonGrossReturnBps);
            Assert.Equal(StartUtc.AddSeconds(1), result.MaximumEventTimeUtc);
        }

        [Fact]
        public void TargetTouchAloneDoesNotGuaranteeLimitExit()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5),
                new[]
                {
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 100.12m, 100.12m, 100.12m, "touch"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.02m, 100.02m, 100.02m, "timer")
                });

            Assert.Equal(TargetStopOutcome.Neither, result.TargetStopOutcome);
            Assert.Equal(2m, result.GrossReturnBps);
        }

        [Fact]
        public void OppositeExitsAtOneTimestampRemainAmbiguousRegardlessOfEventIdOrder()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5),
                new[]
                {
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 99.90m, 99.90m, 99.90m, "a-stop"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 100.13m, 100.13m, 100.13m, "z-target"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.01m, 100.01m, 100.01m, "timer")
                });

            Assert.Equal(CandidateLabelStatus.Ambiguous, result.Status);
            Assert.Null(result.NetReturnBps);
        }

        [Fact]
        public void LaterAmbiguityCannotOverrideAnEarlierStop()
        {
            var result = CreateCalculator().Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5),
                new[]
                {
                    new CandidatePriceObservation(StartUtc.AddSeconds(1), 99.90m, 99.90m, 99.90m, "first-stop"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(3), 99.80m, 99.80m, 99.80m, "later-stop"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(3), 100.20m, 100.20m, 100.20m, "later-target"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(5), 100.01m, 100.01m, 100.01m, "timer")
                });

            Assert.Equal(CandidateLabelStatus.Complete, result.Status);
            Assert.Equal(TargetStopOutcome.StopFirst, result.TargetStopOutcome);
        }

        [Fact]
        public void SpreadObservedAfterStopDoesNotChangeExitCost()
        {
            var prices = new[]
            {
                new CandidatePriceObservation(StartUtc.AddSeconds(1), 99.90m, 99.90m, 99.90m, "stop"),
                new CandidatePriceObservation(StartUtc.AddSeconds(5), 100m, 100m, 100m, "timer")
            };
            var early = new CandidateSpreadObservation(StartUtc.AddSeconds(1), 99.99m, 100.01m);
            var calculator = CreateCalculator();
            var baseline = calculator.Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5), prices, new[] { early });
            var widenedLater = calculator.Calculate(TradeDirection.Long, 100m, StartUtc,
                StartUtc.AddSeconds(5), StartUtc.AddSeconds(5), prices,
                new[] { early, new CandidateSpreadObservation(StartUtc.AddSeconds(4), 99m, 101m) });

            Assert.Equal(baseline.EstimatedCostBps, widenedLater.EstimatedCostBps);
            Assert.Equal(baseline.NetReturnBps, widenedLater.NetReturnBps);
        }

        private static CandidateLabelCalculator CreateCalculator() => new(new CandidateLabelingSettings
        {
            TargetMoveBps = 12m,
            StopMoveBps = 8m,
            CommissionPerSideBps = 0.5m,
            SlippagePerSideBps = 0.75m,
            FallbackRoundTripSpreadBps = 1.5m
        });
    }
}
