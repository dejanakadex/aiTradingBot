namespace TradingBot.Application.Configuration
{
    public sealed class ResearchCalibrationSettings
    {
        public int MinimumCalibrationSamples { get; set; } = 100;
        public int MinimumCalibrationFoldCount { get; set; } = 3;
        public int MinimumCalibrationBinSize { get; set; } = 20;
        public int CalibrationMetricBins { get; set; } = 10;
        public decimal MaximumStableThresholdRange { get; set; } = 0.20m;
        public decimal StableThresholdTolerance { get; set; } = 0.05m;
        public decimal MinimumFoldAgreementRatio { get; set; } = 0.67m;
        public decimal MaximumOutOfSampleBrierDegradation { get; set; } = 0.01m;
        public decimal MaximumHoldoutExpectancyDegradationBps { get; set; } = 0m;
        public decimal MaximumHoldoutDrawdownIncreaseBps { get; set; } = 0m;
        public decimal MinimumHoldoutSampleRatio { get; set; } = 0.50m;
        public int MinimumApprovalReasonLength { get; set; } = 10;
        public int MaximumRankingCandidates { get; set; } = 1_000;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (MinimumCalibrationSamples <= 0) errors.Add("ResearchCalibration:MinimumCalibrationSamples must be greater than zero.");
            if (MinimumCalibrationFoldCount <= 0) errors.Add("ResearchCalibration:MinimumCalibrationFoldCount must be greater than zero.");
            if (MinimumCalibrationBinSize <= 0) errors.Add("ResearchCalibration:MinimumCalibrationBinSize must be greater than zero.");
            if (CalibrationMetricBins <= 1) errors.Add("ResearchCalibration:CalibrationMetricBins must be greater than one.");
            if (MaximumStableThresholdRange is < 0m or > 1m) errors.Add("ResearchCalibration:MaximumStableThresholdRange must be between zero and one.");
            if (StableThresholdTolerance is < 0m or > 1m) errors.Add("ResearchCalibration:StableThresholdTolerance must be between zero and one.");
            if (MinimumFoldAgreementRatio is <= 0m or > 1m) errors.Add("ResearchCalibration:MinimumFoldAgreementRatio must be greater than zero and at most one.");
            if (MaximumOutOfSampleBrierDegradation < 0m) errors.Add("ResearchCalibration:MaximumOutOfSampleBrierDegradation cannot be negative.");
            if (MaximumHoldoutExpectancyDegradationBps < 0m) errors.Add("ResearchCalibration:MaximumHoldoutExpectancyDegradationBps cannot be negative.");
            if (MaximumHoldoutDrawdownIncreaseBps < 0m) errors.Add("ResearchCalibration:MaximumHoldoutDrawdownIncreaseBps cannot be negative.");
            if (MinimumHoldoutSampleRatio is <= 0m or > 1m) errors.Add("ResearchCalibration:MinimumHoldoutSampleRatio must be greater than zero and at most one.");
            if (MinimumApprovalReasonLength <= 0) errors.Add("ResearchCalibration:MinimumApprovalReasonLength must be greater than zero.");
            if (MaximumRankingCandidates <= 0) errors.Add("ResearchCalibration:MaximumRankingCandidates must be greater than zero.");
            return errors;
        }
    }
}
