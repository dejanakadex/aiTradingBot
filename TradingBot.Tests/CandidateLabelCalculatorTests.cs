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
                    new CandidatePriceObservation(StartUtc.AddSeconds(15), 100.20m, 100.05m, 100.12m, "trade-2"),
                    new CandidatePriceObservation(StartUtc.AddSeconds(16), 300m, 1m, 300m, "future")
                },
                new[] { new CandidateSpreadObservation(StartUtc.AddSeconds(1), 99.99m, 100.01m) });

            Assert.Equal(CandidateLabelStatus.Complete, result.Status);
            Assert.Equal(TargetStopOutcome.TargetFirst, result.TargetStopOutcome);
            Assert.Equal(2, result.ObservationCount);
            Assert.Equal(20m, result.MaximumFavorableExcursionBps);
            Assert.Equal(5m, result.MaximumAdverseExcursionBps);
            Assert.Equal(12m, result.GrossReturnBps);
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
