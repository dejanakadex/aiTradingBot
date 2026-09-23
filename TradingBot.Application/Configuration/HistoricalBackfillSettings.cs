namespace TradingBot.Application.Configuration
{
    public sealed class HistoricalBackfillSettings
    {
        public bool Enabled { get; set; } = true;
        public int WorkerIntervalSeconds { get; set; } = 2;
        public int PacingDelayMilliseconds { get; set; } = 11000;
        public int MaximumAttemptsPerSegment { get; set; } = 5;
        public int InitialRetryDelaySeconds { get; set; } = 5;
        public HistoricalBackfillTimeframeSettings OneMinute { get; set; } = new() { LookbackDays = 365, SegmentDays = 1 };
        public HistoricalBackfillTimeframeSettings FiveMinutes { get; set; } = new() { LookbackDays = 730, SegmentDays = 7 };
        public HistoricalBackfillTimeframeSettings FifteenMinutes { get; set; } = new() { LookbackDays = 1825, SegmentDays = 14 };

        public HistoricalBackfillTimeframeSettings GetTimeframe(string timeframe)
        {
            return timeframe.Trim().ToLowerInvariant() switch
            {
                "1m" => OneMinute,
                "5m" => FiveMinutes,
                "15m" => FifteenMinutes,
                _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported historical timeframe.")
            };
        }

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (WorkerIntervalSeconds <= 0) errors.Add("HistoricalBackfill:WorkerIntervalSeconds must be greater than zero.");
            if (PacingDelayMilliseconds < 0) errors.Add("HistoricalBackfill:PacingDelayMilliseconds cannot be negative.");
            if (MaximumAttemptsPerSegment <= 0) errors.Add("HistoricalBackfill:MaximumAttemptsPerSegment must be greater than zero.");
            if (InitialRetryDelaySeconds <= 0) errors.Add("HistoricalBackfill:InitialRetryDelaySeconds must be greater than zero.");
            ValidateTimeframe(OneMinute, "OneMinute", errors);
            ValidateTimeframe(FiveMinutes, "FiveMinutes", errors);
            ValidateTimeframe(FifteenMinutes, "FifteenMinutes", errors);
            return errors;
        }

        private static void ValidateTimeframe(HistoricalBackfillTimeframeSettings? settings, string name, ICollection<string> errors)
        {
            if (settings == null)
            {
                errors.Add($"HistoricalBackfill:{name} is required.");
                return;
            }

            if (settings.LookbackDays <= 0) errors.Add($"HistoricalBackfill:{name}:LookbackDays must be greater than zero.");
            if (settings.SegmentDays <= 0) errors.Add($"HistoricalBackfill:{name}:SegmentDays must be greater than zero.");
            if (settings.SegmentDays > settings.LookbackDays) errors.Add($"HistoricalBackfill:{name}:SegmentDays cannot exceed LookbackDays.");
        }
    }

    public sealed class HistoricalBackfillTimeframeSettings
    {
        public int LookbackDays { get; set; }
        public int SegmentDays { get; set; }
    }
}
