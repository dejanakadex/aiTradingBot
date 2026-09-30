namespace TradingBot.Application.Configuration
{
    public sealed class NumericalModelSettings
    {
        public int RandomSeed { get; set; } = 1701;
        public int NumberOfLeaves { get; set; } = 16;
        public int NumberOfIterations { get; set; } = 100;
        public double LearningRate { get; set; } = 0.05;
        public int MinimumExampleCountPerLeaf { get; set; } = 20;
        public int MinimumTrainingSamples { get; set; } = 100;
        public int MinimumValidationSelections { get; set; } = 20;
        public int MinimumHoldoutSelections { get; set; } = 20;
        public decimal MinimumWalkForwardExpectancyImprovementBps { get; set; } = 0.25m;
        public decimal MinimumHoldoutExpectancyImprovementBps { get; set; } = 0m;
        public decimal[] ProbabilityThresholds { get; set; } = [0.50m, 0.55m, 0.60m, 0.65m, 0.70m, 0.75m];

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (RandomSeed < 0) errors.Add("RandomSeed cannot be negative.");
            if (NumberOfLeaves < 2) errors.Add("NumberOfLeaves must be at least 2.");
            if (NumberOfIterations < 1) errors.Add("NumberOfIterations must be positive.");
            if (LearningRate is <= 0d or > 1d) errors.Add("LearningRate must be greater than zero and at most one.");
            if (MinimumExampleCountPerLeaf < 1) errors.Add("MinimumExampleCountPerLeaf must be positive.");
            if (MinimumTrainingSamples < 20) errors.Add("MinimumTrainingSamples must be at least 20.");
            if (MinimumValidationSelections < 1 || MinimumHoldoutSelections < 1) errors.Add("Minimum selection counts must be positive.");
            if (MinimumWalkForwardExpectancyImprovementBps < 0m || MinimumHoldoutExpectancyImprovementBps < 0m) errors.Add("Expectancy improvements cannot be negative.");
            if (ProbabilityThresholds is not { Length: > 0 } || ProbabilityThresholds.Any(x => x is <= 0m or >= 1m)) errors.Add("ProbabilityThresholds must contain values strictly between zero and one.");
            if ((ProbabilityThresholds ?? []).Distinct().Count() != (ProbabilityThresholds?.Length ?? 0)) errors.Add("ProbabilityThresholds cannot contain duplicates.");
            return errors;
        }
    }
}
