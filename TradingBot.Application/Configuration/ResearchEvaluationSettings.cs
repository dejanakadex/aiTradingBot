namespace TradingBot.Application.Configuration
{
    public sealed class ResearchEvaluationSettings
    {
        public int TrainingWindowDays { get; set; } = 60;
        public int ValidationWindowDays { get; set; } = 14;
        public int StepDays { get; set; } = 14;
        public int HoldoutDays { get; set; } = 30;
        public int MinimumTrainingSamples { get; set; } = 100;
        public int MinimumValidationSamples { get; set; } = 30;
        public int MinimumHoldoutSamples { get; set; } = 50;
        public int MaximumCandidates { get; set; } = 250_000;
        public decimal[] ConfidenceThresholds { get; set; } = new[] { 0m, 0.50m, 0.60m, 0.70m, 0.80m, 0.90m };
        public decimal[] CostStressMultipliers { get; set; } = new[] { 1m, 1.25m, 1.5m, 2m };
        public decimal LowLiquidityUpperBound { get; set; } = 0.75m;
        public decimal HighLiquidityLowerBound { get; set; } = 1.25m;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (TrainingWindowDays <= 0) errors.Add("ResearchEvaluation:TrainingWindowDays must be greater than zero.");
            if (ValidationWindowDays <= 0) errors.Add("ResearchEvaluation:ValidationWindowDays must be greater than zero.");
            if (StepDays < ValidationWindowDays) errors.Add("ResearchEvaluation:StepDays must be greater than or equal to ValidationWindowDays to prevent overlapping validation folds.");
            if (HoldoutDays <= 0) errors.Add("ResearchEvaluation:HoldoutDays must be greater than zero.");
            if (MinimumTrainingSamples <= 0 || MinimumValidationSamples <= 0 || MinimumHoldoutSamples <= 0) errors.Add("ResearchEvaluation minimum sample sizes must be greater than zero.");
            if (MaximumCandidates <= 0) errors.Add("ResearchEvaluation:MaximumCandidates must be greater than zero.");
            if (ConfidenceThresholds is not { Length: > 0 } || ConfidenceThresholds.Any(value => value is < 0m or > 1m)) errors.Add("ResearchEvaluation:ConfidenceThresholds must contain values between zero and one.");
            if ((ConfidenceThresholds ?? Array.Empty<decimal>()).Distinct().Count() != (ConfidenceThresholds?.Length ?? 0)) errors.Add("ResearchEvaluation:ConfidenceThresholds cannot contain duplicates.");
            if (CostStressMultipliers is not { Length: > 0 } || CostStressMultipliers.Any(value => value < 0m)) errors.Add("ResearchEvaluation:CostStressMultipliers must contain non-negative values.");
            if ((CostStressMultipliers ?? Array.Empty<decimal>()).Distinct().Count() != (CostStressMultipliers?.Length ?? 0)) errors.Add("ResearchEvaluation:CostStressMultipliers cannot contain duplicates.");
            if (LowLiquidityUpperBound < 0m || HighLiquidityLowerBound <= LowLiquidityUpperBound) errors.Add("ResearchEvaluation liquidity bounds are invalid.");
            return errors;
        }
    }
}
