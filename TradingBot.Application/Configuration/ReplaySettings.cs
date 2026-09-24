namespace TradingBot.Application.Configuration
{
    public sealed class ReplaySettings
    {
        public bool Enabled { get; set; } = true;
        public int WorkerPollIntervalMilliseconds { get; set; } = 250;
        public int EventsPerBatch { get; set; } = 250;
        public int HistoryCandles { get; set; } = 80;
        public int MaximumDelayMilliseconds { get; set; } = 5_000;
        public decimal MaximumSpeedMultiplier { get; set; } = 10_000m;

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (WorkerPollIntervalMilliseconds <= 0) errors.Add("Replay:WorkerPollIntervalMilliseconds must be greater than zero.");
            if (EventsPerBatch <= 0) errors.Add("Replay:EventsPerBatch must be greater than zero.");
            if (HistoryCandles < 2) errors.Add("Replay:HistoryCandles must be at least 2.");
            if (MaximumDelayMilliseconds < 0) errors.Add("Replay:MaximumDelayMilliseconds cannot be negative.");
            if (MaximumSpeedMultiplier <= 0m) errors.Add("Replay:MaximumSpeedMultiplier must be greater than zero.");
            return errors;
        }
    }
}
