namespace TradingBot.Application.Configuration
{
    public sealed class CandidateLabelingSettings
    {
        public bool Enabled { get; set; } = true;
        public int WorkerIntervalSeconds { get; set; } = 5;
        public int BatchSize { get; set; } = 100;
        public int DataAvailabilityGraceSeconds { get; set; } = 30;
        public int[] HorizonsSeconds { get; set; } = new[] { 5, 15, 30, 60, 180, 300 };
        public decimal TargetMoveBps { get; set; } = 12m;
        public decimal StopMoveBps { get; set; } = 8m;
        public decimal CommissionPerSideBps { get; set; } = 0.5m;
        public decimal SlippagePerSideBps { get; set; } = 0.75m;
        public decimal FallbackRoundTripSpreadBps { get; set; } = 1.5m;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (WorkerIntervalSeconds <= 0) errors.Add("CandidateLabeling:WorkerIntervalSeconds must be greater than zero.");
            if (BatchSize <= 0) errors.Add("CandidateLabeling:BatchSize must be greater than zero.");
            if (DataAvailabilityGraceSeconds < 0) errors.Add("CandidateLabeling:DataAvailabilityGraceSeconds cannot be negative.");
            if (HorizonsSeconds is not { Length: > 0 } || HorizonsSeconds.Any(value => value <= 0)) errors.Add("CandidateLabeling:HorizonsSeconds must contain positive values.");
            if ((HorizonsSeconds ?? Array.Empty<int>()).Distinct().Count() != (HorizonsSeconds?.Length ?? 0)) errors.Add("CandidateLabeling:HorizonsSeconds cannot contain duplicates.");
            if (TargetMoveBps <= 0m) errors.Add("CandidateLabeling:TargetMoveBps must be greater than zero.");
            if (StopMoveBps <= 0m) errors.Add("CandidateLabeling:StopMoveBps must be greater than zero.");
            if (CommissionPerSideBps < 0m || SlippagePerSideBps < 0m || FallbackRoundTripSpreadBps < 0m) errors.Add("CandidateLabeling cost assumptions cannot be negative.");
            return errors;
        }
    }
}
