using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class CandidateLabelCalculator
    {
        private readonly CandidateLabelingSettings _settings;

        public CandidateLabelCalculator(CandidateLabelingSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public CandidateLabelCalculation Calculate(
            TradeDirection direction,
            decimal entryPrice,
            DateTime candidateTimeUtc,
            DateTime windowEndUtc,
            DateTime asOfUtc,
            IReadOnlyCollection<CandidatePriceObservation> priceObservations,
            IReadOnlyCollection<CandidateSpreadObservation>? spreadObservations = null)
        {
            if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
            if (entryPrice <= 0m) throw new ArgumentOutOfRangeException(nameof(entryPrice));
            var start = ToUtc(candidateTimeUtc);
            var end = ToUtc(windowEndUtc);
            var asOf = ToUtc(asOfUtc);
            if (end <= start) throw new ArgumentException("Label window end must be after candidate time.", nameof(windowEndUtc));
            if (asOf < end)
            {
                return new CandidateLabelCalculation
                {
                    Status = CandidateLabelStatus.Pending,
                    Reasons = new[] { "Label horizon has not elapsed." }
                };
            }

            var prices = (priceObservations ?? Array.Empty<CandidatePriceObservation>())
                .Where(item => ToUtc(item.EventTimeUtc) > start && ToUtc(item.EventTimeUtc) <= end)
                .Where(item => item.High > 0m && item.Low > 0m && item.Close > 0m && item.High >= item.Low)
                .OrderBy(item => item.EventTimeUtc)
                .ThenBy(item => item.SourceEventId, StringComparer.Ordinal)
                .ToArray();
            if (prices.Length == 0)
            {
                return new CandidateLabelCalculation
                {
                    Status = CandidateLabelStatus.InsufficientData,
                    Reasons = new[] { "No valid price observation exists strictly after candidate time and within the completed horizon." }
                };
            }

            var spreads = (spreadObservations ?? Array.Empty<CandidateSpreadObservation>())
                .Where(item => ToUtc(item.EventTimeUtc) > start && ToUtc(item.EventTimeUtc) <= end)
                .Where(item => item.Bid > 0m && item.Ask >= item.Bid)
                .OrderBy(item => item.EventTimeUtc)
                .ToArray();
            var observedSpreadBps = spreads.Length == 0
                ? (decimal?)null
                : spreads.Average(item => (item.Ask - item.Bid) / ((item.Ask + item.Bid) / 2m) * 10_000m);
            var roundTripSpreadBps = observedSpreadBps ?? _settings.FallbackRoundTripSpreadBps;
            var estimatedCostBps = roundTripSpreadBps
                + 2m * _settings.CommissionPerSideBps
                + 2m * _settings.SlippagePerSideBps;

            decimal maximumFavorable = 0m;
            decimal maximumAdverse = 0m;
            var targetStop = TargetStopOutcome.Neither;
            var firstTargetEventId = string.Empty;
            var firstStopEventId = string.Empty;
            foreach (var observation in prices)
            {
                var favorable = direction == TradeDirection.Long
                    ? ToBps(observation.High - entryPrice, entryPrice)
                    : ToBps(entryPrice - observation.Low, entryPrice);
                var adverse = direction == TradeDirection.Long
                    ? ToBps(entryPrice - observation.Low, entryPrice)
                    : ToBps(observation.High - entryPrice, entryPrice);
                maximumFavorable = Math.Max(maximumFavorable, favorable);
                maximumAdverse = Math.Max(maximumAdverse, adverse);

                var targetHit = favorable >= _settings.TargetMoveBps;
                var stopHit = adverse >= _settings.StopMoveBps;
                if (targetHit && firstTargetEventId.Length == 0) firstTargetEventId = observation.SourceEventId;
                if (stopHit && firstStopEventId.Length == 0) firstStopEventId = observation.SourceEventId;
                if (targetStop == TargetStopOutcome.Neither) targetStop = (targetHit, stopHit) switch
                {
                    (true, true) when observation.IsBar => TargetStopOutcome.Ambiguous,
                    (true, true) => TargetStopOutcome.Ambiguous,
                    (true, false) => TargetStopOutcome.TargetFirst,
                    (false, true) => TargetStopOutcome.StopFirst,
                    _ => TargetStopOutcome.Neither
                };
            }

            var exitPrice = prices[^1].Close;
            var grossReturnBps = direction == TradeDirection.Long
                ? ToBps(exitPrice - entryPrice, entryPrice)
                : ToBps(entryPrice - exitPrice, entryPrice);
            var status = targetStop == TargetStopOutcome.Ambiguous
                ? CandidateLabelStatus.Ambiguous
                : CandidateLabelStatus.Complete;
            var reasons = new List<string>
            {
                "Only observations strictly after candidate time and no later than the horizon end were used.",
                observedSpreadBps.HasValue
                    ? "Cost uses observed spread plus configured round-trip commission and slippage."
                    : "Cost uses configured fallback spread plus round-trip commission and slippage."
            };
            if (status == CandidateLabelStatus.Ambiguous)
            {
                reasons.Add("Target and stop were both touched by the same observation; their intrabar order is unknown.");
            }

            return new CandidateLabelCalculation
            {
                Status = status,
                TargetStopOutcome = targetStop,
                ObservationCount = prices.Length,
                ExitPrice = exitPrice,
                MaximumFavorableExcursionBps = Math.Max(0m, maximumFavorable),
                MaximumAdverseExcursionBps = Math.Max(0m, maximumAdverse),
                GrossReturnBps = grossReturnBps,
                EstimatedCostBps = estimatedCostBps,
                NetReturnBps = grossReturnBps - estimatedCostBps,
                ObservedSpreadBps = observedSpreadBps,
                FirstTargetEventId = firstTargetEventId,
                FirstStopEventId = firstStopEventId,
                MaximumEventTimeUtc = ToUtc(prices[^1].EventTimeUtc),
                Reasons = reasons
            };
        }

        private static decimal ToBps(decimal difference, decimal reference) => difference / reference * 10_000m;

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
