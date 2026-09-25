using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ResearchEvaluationCalculator
    {
        private readonly ResearchEvaluationSettings _settings;

        public ResearchEvaluationCalculator(ResearchEvaluationSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public ResearchEvaluationCalculation Calculate(
            IReadOnlyCollection<ResearchEvaluationObservation> source,
            DateTime fromUtc,
            DateTime toUtc)
        {
            var from = ToUtc(fromUtc);
            var to = ToUtc(toUtc);
            if (from >= to) throw new ArgumentException("Evaluation range start must be earlier than its end.");
            var observations = (source ?? Array.Empty<ResearchEvaluationObservation>())
                .Where(item => item.EvaluatedAtUtc >= from && item.LabelWindowEndUtc <= to)
                .OrderBy(item => item.EvaluatedAtUtc)
                .ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
                .ToArray();
            if (observations.Length == 0) throw new InvalidOperationException("No completed candidate labels exist in the requested evaluation range.");

            var eligible = observations.Where(item => item.Outcome != ResearchCandidateOutcome.Rejected).ToArray();
            var featureVersion = SingleVersion(eligible, item => item.FeatureVersion, "feature");
            var patternVersion = SingleVersion(eligible, item => item.PatternVersion, "pattern");
            var labelVersion = SingleVersion(eligible, item => item.LabelVersion, "label");
            var holdoutStart = to.AddDays(-_settings.HoldoutDays);
            if (holdoutStart <= from.AddDays(_settings.TrainingWindowDays + _settings.ValidationWindowDays))
            {
                throw new InvalidOperationException("Evaluation range is too short for the configured training, validation and untouched holdout windows.");
            }

            // Purge labels whose future-looking horizon crosses a split boundary.
            var preHoldout = eligible.Where(item => item.EvaluatedAtUtc >= from && item.LabelWindowEndUtc <= holdoutStart).ToArray();
            var holdout = eligible.Where(item => item.EvaluatedAtUtc >= holdoutStart && item.LabelWindowEndUtc <= to).ToArray();
            if (preHoldout.Length < _settings.MinimumTrainingSamples)
            {
                throw new InvalidOperationException($"Pre-holdout range has {preHoldout.Length} eligible samples; at least {_settings.MinimumTrainingSamples} are required.");
            }
            if (holdout.Length < _settings.MinimumHoldoutSamples)
            {
                throw new InvalidOperationException($"Untouched holdout has {holdout.Length} eligible samples; at least {_settings.MinimumHoldoutSamples} are required.");
            }

            var preThresholds = EvaluateThresholds(preHoldout);
            var selected = SelectThreshold(preThresholds, _settings.MinimumTrainingSamples);
            var finalPreSelected = ApplyThreshold(preHoldout, selected.ConfidenceThreshold);
            var folds = new List<WalkForwardFoldResult>();
            var walkForwardSelected = new List<ResearchEvaluationObservation>();
            var validationStart = from.AddDays(_settings.TrainingWindowDays);
            var foldNumber = 1;
            while (validationStart.AddDays(_settings.ValidationWindowDays) <= holdoutStart)
            {
                var validationEnd = validationStart.AddDays(_settings.ValidationWindowDays);
                var trainingStart = validationStart.AddDays(-_settings.TrainingWindowDays);
                var training = eligible.Where(item => item.EvaluatedAtUtc >= trainingStart && item.LabelWindowEndUtc <= validationStart).ToArray();
                var validation = eligible.Where(item => item.EvaluatedAtUtc >= validationStart && item.LabelWindowEndUtc <= validationEnd).ToArray();
                if (training.Length < _settings.MinimumTrainingSamples || validation.Length < _settings.MinimumValidationSamples)
                {
                    folds.Add(new WalkForwardFoldResult(
                        foldNumber, trainingStart, validationStart, validationStart, validationEnd,
                        training.Length, validation.Length, null,
                        $"Skipped: requires at least {_settings.MinimumTrainingSamples} training and {_settings.MinimumValidationSamples} validation samples.",
                        null, null));
                }
                else
                {
                    var foldSelection = SelectThreshold(EvaluateThresholds(training), _settings.MinimumTrainingSamples);
                    var trainingSelected = ApplyThreshold(training, foldSelection.ConfidenceThreshold);
                    var validationSelected = ApplyThreshold(validation, foldSelection.ConfidenceThreshold);
                    walkForwardSelected.AddRange(validationSelected);
                    folds.Add(new WalkForwardFoldResult(
                        foldNumber, trainingStart, validationStart, validationStart, validationEnd,
                        training.Length, validation.Length, foldSelection.ConfidenceThreshold,
                        foldSelection.Reason,
                        CalculateMetrics(trainingSelected), CalculateMetrics(validationSelected)));
                }
                foldNumber++;
                validationStart = validationStart.AddDays(_settings.StepDays);
            }
            if (!folds.Any(item => item.SelectedConfidenceThreshold.HasValue))
            {
                throw new InvalidOperationException("No walk-forward fold contains enough training and validation samples.");
            }

            var holdoutSelected = ApplyThreshold(holdout, selected.ConfidenceThreshold);
            var outOfSample = walkForwardSelected.Concat(holdoutSelected)
                .GroupBy(item => item.CandidateId)
                .Select(group => group.First())
                .OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
                .ToArray();

            return new ResearchEvaluationCalculation
            {
                FromUtc = from,
                ToUtc = to,
                HoldoutStartUtc = holdoutStart,
                SourceCandidateCount = observations.Length,
                EligibleCandidateCount = eligible.Length,
                FeatureVersion = featureVersion,
                PatternVersion = patternVersion,
                LabelVersion = labelVersion,
                SelectedConfidenceThreshold = selected.ConfidenceThreshold,
                SelectionReason = selected.Reason,
                PreHoldoutMetrics = CalculateMetrics(finalPreSelected),
                WalkForwardMetrics = CalculateMetrics(walkForwardSelected),
                HoldoutMetrics = CalculateMetrics(holdoutSelected),
                PreHoldoutThresholds = preThresholds,
                Folds = folds,
                CostSensitivity = _settings.CostStressMultipliers.OrderBy(value => value)
                    .Select(multiplier => new CostSensitivityResult(multiplier, CalculateMetrics(
                        holdoutSelected.Select(item => item with
                        {
                            NetReturnBps = item.GrossReturnBps - item.EstimatedCostBps * multiplier
                        }))))
                    .ToArray(),
                Segments = BuildSegments(outOfSample),
                WalkForwardCandidateIds = walkForwardSelected.Select(item => item.CandidateId).Distinct().OrderBy(value => value).ToArray(),
                HoldoutCandidateIds = holdoutSelected.Select(item => item.CandidateId).Distinct().OrderBy(value => value).ToArray()
            };
        }

        public EvaluationMetrics CalculateMetrics(IEnumerable<ResearchEvaluationObservation> source)
        {
            var values = source.OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.CandidateKey, StringComparer.Ordinal).ToArray();
            if (values.Length == 0) return ResearchEvaluationCalculation.EmptyMetrics;
            var returns = values.Select(item => item.NetReturnBps).ToArray();
            var wins = returns.Count(value => value > 0m);
            var losses = returns.Count(value => value < 0m);
            var breakEven = returns.Length - wins - losses;
            var grossProfit = returns.Where(value => value > 0m).Sum();
            var grossLoss = Math.Abs(returns.Where(value => value < 0m).Sum());
            decimal cumulative = 0m;
            decimal peak = 0m;
            decimal maximumDrawdown = 0m;
            foreach (var value in returns)
            {
                cumulative += value;
                peak = Math.Max(peak, cumulative);
                maximumDrawdown = Math.Max(maximumDrawdown, peak - cumulative);
            }
            return new EvaluationMetrics(
                values.Length,
                wins,
                losses,
                breakEven,
                Decimal(values.Length == 0 ? 0d : (double)wins / values.Length),
                returns.Average(),
                returns.Sum(),
                grossProfit,
                grossLoss,
                grossLoss > 0m ? grossProfit / grossLoss : null,
                maximumDrawdown,
                values.Average(item => item.MaximumFavorableExcursionBps),
                values.Average(item => item.MaximumAdverseExcursionBps),
                Decimal((double)values.Count(item => item.TargetStopOutcome == TargetStopOutcome.TargetFirst) / values.Length),
                Decimal((double)values.Count(item => item.TargetStopOutcome == TargetStopOutcome.StopFirst) / values.Length),
                Decimal((double)values.Count(item => item.TargetStopOutcome == TargetStopOutcome.Ambiguous) / values.Length));
        }

        private IReadOnlyList<ThresholdEvaluation> EvaluateThresholds(IReadOnlyCollection<ResearchEvaluationObservation> source) =>
            _settings.ConfidenceThresholds.OrderBy(value => value)
                .Select(threshold => new ThresholdEvaluation(threshold, CalculateMetrics(ApplyThreshold(source, threshold))))
                .ToArray();

        private static ResearchEvaluationObservation[] ApplyThreshold(IEnumerable<ResearchEvaluationObservation> source, decimal threshold) =>
            source.Where(item => item.Confidence >= threshold)
                .OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.CandidateKey, StringComparer.Ordinal).ToArray();

        private static (decimal ConfidenceThreshold, string Reason) SelectThreshold(
            IReadOnlyCollection<ThresholdEvaluation> evaluations,
            int minimumSamples)
        {
            var candidates = evaluations.Where(item => item.Metrics.SampleCount >= minimumSamples).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException($"No confidence threshold retains the required {minimumSamples} samples.");
            var profitable = candidates.Where(item => item.Metrics.ExpectancyBps > 0m).ToArray();
            var pool = profitable.Length > 0 ? profitable : candidates;
            var selected = pool
                .OrderByDescending(item => item.Metrics.ExpectancyBps)
                .ThenByDescending(item => ProfitFactorRank(item.Metrics))
                .ThenBy(item => item.Metrics.MaximumDrawdownBps)
                .ThenByDescending(item => item.Metrics.SampleCount)
                .ThenBy(item => item.ConfidenceThreshold)
                .First();
            var profitability = profitable.Length > 0
                ? "positive-expectancy thresholds were preferred"
                : "no threshold had positive expectancy, so the least-negative eligible threshold was retained";
            return (selected.ConfidenceThreshold,
                $"Selected without using win rate as the objective: {profitability}; ranked by expectancy, profit factor, lower drawdown and sample count.");
        }

        private IReadOnlyList<EvaluationSegmentResult> BuildSegments(IReadOnlyCollection<ResearchEvaluationObservation> source)
        {
            var dimensions = new (string Name, Func<ResearchEvaluationObservation, string> Value)[]
            {
                ("Instrument", item => item.InstrumentId),
                ("Strategy", item => item.StrategyId),
                ("Pattern", item => item.PatternType.ToString()),
                ("Direction", item => item.Direction.ToString()),
                ("Timeframe", item => item.Timeframe.ToString()),
                ("UtcWeekday", item => item.EvaluatedAtUtc.DayOfWeek.ToString()),
                ("NewYorkSession", item => NewYorkSession(item.EvaluatedAtUtc)),
                ("MarketRegime", item => string.IsNullOrWhiteSpace(item.MarketRegime) ? MarketRegime.Unknown.ToString() : item.MarketRegime),
                ("Liquidity", item => LiquidityBucket(item.NormalizedLiquidity))
            };
            return dimensions.SelectMany(dimension => source
                    .GroupBy(dimension.Value, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new EvaluationSegmentResult(dimension.Name, group.Key, CalculateMetrics(group))))
                .ToArray();
        }

        private string LiquidityBucket(decimal? value) => !value.HasValue
            ? "Unknown"
            : value.Value < _settings.LowLiquidityUpperBound
                ? "Low"
                : value.Value >= _settings.HighLiquidityLowerBound ? "High" : "Normal";

        private static string NewYorkSession(DateTime timestampUtc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(ToUtc(timestampUtc), ResolveEasternTimeZone());
            var time = local.TimeOfDay;
            if (time < new TimeSpan(9, 30, 0)) return "PreMarket";
            if (time < new TimeSpan(10, 30, 0)) return "OpeningHour";
            if (time < new TimeSpan(15, 0, 0)) return "Midday";
            if (time < new TimeSpan(16, 0, 0)) return "PowerHour";
            return "AfterHours";
        }

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            throw new InvalidOperationException("New York time zone is not available.");
        }

        private static string SingleVersion(
            IReadOnlyCollection<ResearchEvaluationObservation> source,
            Func<ResearchEvaluationObservation, string> selector,
            string name)
        {
            var versions = source.Select(selector).Distinct(StringComparer.Ordinal).ToArray();
            if (versions.Length != 1) throw new InvalidOperationException($"Evaluation requires exactly one {name} version; found {versions.Length}.");
            return versions[0];
        }

        private static decimal ProfitFactorRank(EvaluationMetrics metrics) => metrics.ProfitFactor
            ?? (metrics.GrossProfitBps > 0m && metrics.GrossLossBps == 0m ? decimal.MaxValue : 0m);
        private static decimal Decimal(double value) => Convert.ToDecimal(value);
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
