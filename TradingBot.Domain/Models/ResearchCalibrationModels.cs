namespace TradingBot.Domain.Models
{
    public sealed record ProbabilityCalibrationPoint(
        decimal MinimumConfidence,
        decimal MaximumConfidence,
        decimal CalibratedProbability,
        int SampleCount,
        decimal ObservedWinRate,
        decimal AverageNetReturnBps);

    public sealed record ProbabilityCalibrationMetrics(
        int SampleCount,
        decimal BrierScore,
        decimal LogLoss,
        decimal ExpectedCalibrationError,
        decimal AveragePredictedProbability,
        decimal ObservedWinRate);

    public sealed record ThresholdStabilityResult(
        int FoldCount,
        decimal MinimumThreshold,
        decimal MaximumThreshold,
        decimal MedianThreshold,
        decimal ThresholdRange,
        decimal AgreementRatio,
        bool IsStable,
        string Reason);

    public sealed record CalibrationBaselineComparison(
        decimal BaselineConfidenceThreshold,
        decimal ProposedConfidenceThreshold,
        EvaluationMetrics BaselineHoldoutMetrics,
        EvaluationMetrics ProposedHoldoutMetrics,
        decimal ExpectancyDeltaBps,
        decimal MaximumDrawdownDeltaBps,
        decimal HoldoutSampleRatio,
        decimal OutOfSampleBrierDelta,
        decimal HoldoutBrierDelta,
        bool PassesApprovalGate,
        IReadOnlyList<string> Reasons);

    public sealed class ResearchCalibrationCalculation
    {
        public IReadOnlyList<ProbabilityCalibrationPoint> CalibrationPoints { get; init; } = Array.Empty<ProbabilityCalibrationPoint>();
        public ThresholdStabilityResult ThresholdStability { get; init; } = new(0, 0m, 0m, 0m, 0m, 0m, false, "No folds were evaluated.");
        public decimal StableConfidenceThreshold { get; init; }
        public decimal MinimumCalibratedProbability { get; init; }
        public decimal AverageWinBps { get; init; }
        public decimal AverageLossBps { get; init; }
        public decimal AverageEstimatedCostBps { get; init; }
        public ProbabilityCalibrationMetrics WalkForwardRawMetrics { get; init; } = EmptyCalibrationMetrics;
        public ProbabilityCalibrationMetrics WalkForwardCalibratedMetrics { get; init; } = EmptyCalibrationMetrics;
        public ProbabilityCalibrationMetrics HoldoutRawMetrics { get; init; } = EmptyCalibrationMetrics;
        public ProbabilityCalibrationMetrics HoldoutCalibratedMetrics { get; init; } = EmptyCalibrationMetrics;
        public EvaluationMetrics ProposedHoldoutMetrics { get; init; } = ResearchEvaluationCalculation.EmptyMetrics;
        public CalibrationBaselineComparison BaselineComparison { get; init; } = new(
            0m, 0m, ResearchEvaluationCalculation.EmptyMetrics, ResearchEvaluationCalculation.EmptyMetrics,
            0m, 0m, 0m, 0m, 0m, false, Array.Empty<string>());

        public static ProbabilityCalibrationMetrics EmptyCalibrationMetrics { get; } = new(0, 0m, 0m, 0m, 0m, 0m);
    }
}
