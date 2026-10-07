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
            var ticks = prices.Where(item => !item.IsBar).ToArray();
            var ambiguousBar = prices.Any(item => item.IsBar &&
                (direction == TradeDirection.Long
                    ? ToBps(item.High - entryPrice, entryPrice) >= _settings.TargetMoveBps
                      && ToBps(entryPrice - item.Low, entryPrice) >= _settings.StopMoveBps
                    : ToBps(entryPrice - item.Low, entryPrice) >= _settings.TargetMoveBps
                      && ToBps(item.High - entryPrice, entryPrice) >= _settings.StopMoveBps));
            if (ambiguousBar && ticks.Length == 0)
            {
                return new CandidateLabelCalculation
                {
                    Status = CandidateLabelStatus.Ambiguous,
                    TargetStopOutcome = TargetStopOutcome.Ambiguous,
                    ObservationCount = prices.Length,
                    Reasons = new[] { "Target and stop were touched inside a bar; their intrabar order and executable exit are unknown." }
                };
            }
            if (ticks.Length == 0)
                return Insufficient("No trade ticks exist strictly after candidate time and within the completed horizon.");
            var maximumGap = TimeSpan.FromSeconds(_settings.MaximumTickGapSeconds);
            var previousTime = start;
            foreach (var tick in ticks)
            {
                if (ToUtc(tick.EventTimeUtc) - previousTime > maximumGap)
                    return Insufficient("Trade tick coverage has a gap larger than the configured maximum.");
                previousTime = ToUtc(tick.EventTimeUtc);
            }
            if (end - previousTime > maximumGap)
                return Insufficient("No trade tick covers the end of the completed horizon within the configured maximum gap.");

            foreach (var simultaneous in ticks.GroupBy(item => ToUtc(item.EventTimeUtc)))
            {
                var targetTouched = simultaneous.Any(item => direction == TradeDirection.Long
                    ? ToBps(item.High - entryPrice, entryPrice) > _settings.TargetMoveBps
                    : ToBps(entryPrice - item.Low, entryPrice) > _settings.TargetMoveBps);
                var stopTouched = simultaneous.Any(item => direction == TradeDirection.Long
                    ? ToBps(entryPrice - item.Low, entryPrice) >= _settings.StopMoveBps
                    : ToBps(item.High - entryPrice, entryPrice) >= _settings.StopMoveBps);
                if (targetTouched && stopTouched)
                {
                    return new CandidateLabelCalculation
                    {
                        Status = CandidateLabelStatus.Ambiguous,
                        TargetStopOutcome = TargetStopOutcome.Ambiguous,
                        ObservationCount = ticks.Length,
                        Reasons = new[] { "Target and stop were both touched at the same trade timestamp; execution order is unknown." }
                    };
                }
                if (targetTouched || stopTouched) break;
            }

            decimal maximumFavorable = 0m;
            decimal maximumAdverse = 0m;
            var targetStop = TargetStopOutcome.Neither;
            var firstTargetEventId = string.Empty;
            var firstStopEventId = string.Empty;
            var exitPrice = ticks[^1].Close;
            var exitTime = ToUtc(ticks[^1].EventTimeUtc);
            foreach (var observation in ticks)
            {
                var favorable = direction == TradeDirection.Long
                    ? ToBps(observation.High - entryPrice, entryPrice)
                    : ToBps(entryPrice - observation.Low, entryPrice);
                var adverse = direction == TradeDirection.Long
                    ? ToBps(entryPrice - observation.Low, entryPrice)
                    : ToBps(observation.High - entryPrice, entryPrice);
                maximumFavorable = Math.Max(maximumFavorable, favorable);
                maximumAdverse = Math.Max(maximumAdverse, adverse);

                var targetHit = favorable > _settings.TargetMoveBps;
                var stopHit = adverse >= _settings.StopMoveBps;
                if (targetHit && firstTargetEventId.Length == 0) firstTargetEventId = observation.SourceEventId;
                if (stopHit && firstStopEventId.Length == 0) firstStopEventId = observation.SourceEventId;
                targetStop = (targetHit, stopHit) switch
                {
                    (true, true) => TargetStopOutcome.Ambiguous,
                    (true, false) => TargetStopOutcome.TargetFirst,
                    (false, true) => TargetStopOutcome.StopFirst,
                    _ => TargetStopOutcome.Neither
                };
                if (targetStop != TargetStopOutcome.Neither)
                {
                    exitPrice = targetStop == TargetStopOutcome.TargetFirst
                        ? direction == TradeDirection.Long
                            ? entryPrice * (1m + _settings.TargetMoveBps / 10_000m)
                            : entryPrice * (1m - _settings.TargetMoveBps / 10_000m)
                        : observation.Close;
                    exitTime = ToUtc(observation.EventTimeUtc);
                    break;
                }
            }

            var spreads = (spreadObservations ?? Array.Empty<CandidateSpreadObservation>())
                .Where(item => ToUtc(item.EventTimeUtc) > start && ToUtc(item.EventTimeUtc) <= exitTime)
                .Where(item => item.Bid > 0m && item.Ask >= item.Bid)
                .OrderBy(item => item.EventTimeUtc)
                .ToArray();
            var spreadCovered = spreads.Length > 0
                && ToUtc(spreads[0].EventTimeUtc) - start <= maximumGap
                && exitTime - ToUtc(spreads[^1].EventTimeUtc) <= maximumGap;
            var observedSpreadBps = spreadCovered
                ? spreads.Average(item => (item.Ask - item.Bid) / ((item.Ask + item.Bid) / 2m) * 10_000m)
                : (decimal?)null;
            var roundTripSpreadBps = observedSpreadBps ?? _settings.FallbackRoundTripSpreadBps;
            var estimatedCostBps = roundTripSpreadBps
                + 2m * _settings.CommissionPerSideBps
                + 2m * _settings.SlippagePerSideBps;

            var grossReturnBps = direction == TradeDirection.Long
                ? ToBps(exitPrice - entryPrice, entryPrice)
                : ToBps(entryPrice - exitPrice, entryPrice);
            var horizonPrice = ticks[^1].Close;
            var horizonGrossReturnBps = direction == TradeDirection.Long
                ? ToBps(horizonPrice - entryPrice, entryPrice)
                : ToBps(entryPrice - horizonPrice, entryPrice);
            var status = targetStop == TargetStopOutcome.Ambiguous
                ? CandidateLabelStatus.Ambiguous
                : CandidateLabelStatus.Complete;
            var reasons = new List<string>
            {
                "Only sufficiently covered trade ticks strictly after candidate time and no later than the horizon end were used.",
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
                ObservationCount = ticks.Length,
                ExitPrice = exitPrice,
                HorizonExitPrice = horizonPrice,
                HorizonGrossReturnBps = horizonGrossReturnBps,
                MaximumFavorableExcursionBps = Math.Max(0m, maximumFavorable),
                MaximumAdverseExcursionBps = Math.Max(0m, maximumAdverse),
                GrossReturnBps = grossReturnBps,
                EstimatedCostBps = estimatedCostBps,
                NetReturnBps = grossReturnBps - estimatedCostBps,
                ObservedSpreadBps = observedSpreadBps,
                FirstTargetEventId = firstTargetEventId,
                FirstStopEventId = firstStopEventId,
                MaximumEventTimeUtc = exitTime,
                Reasons = reasons
            };
        }

        private static CandidateLabelCalculation Insufficient(string reason) => new()
        {
            Status = CandidateLabelStatus.InsufficientData,
            Reasons = new[] { reason }
        };

        private static decimal ToBps(decimal difference, decimal reference) => difference / reference * 10_000m;

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
