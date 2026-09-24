namespace TradingBot.Application.Configuration
{
    public sealed class DatasetStorageSettings
    {
        public bool Enabled { get; set; } = true;
        public string RootPath { get; set; } = "data/datasets";
        public int QueueCapacity { get; set; } = 8192;
        public int BatchSize { get; set; } = 1000;
        public int FlushIntervalSeconds { get; set; } = 5;
        public string SchemaVersion { get; set; } = "market-data-v2.parquet-v1";

        public IReadOnlyList<string> GetValidationErrors()
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(RootPath)) errors.Add("DatasetStorage:RootPath is required.");
            if (QueueCapacity <= 0) errors.Add("DatasetStorage:QueueCapacity must be greater than zero.");
            if (BatchSize <= 0) errors.Add("DatasetStorage:BatchSize must be greater than zero.");
            if (BatchSize > QueueCapacity) errors.Add("DatasetStorage:BatchSize cannot exceed QueueCapacity.");
            if (FlushIntervalSeconds <= 0) errors.Add("DatasetStorage:FlushIntervalSeconds must be greater than zero.");
            if (string.IsNullOrWhiteSpace(SchemaVersion)) errors.Add("DatasetStorage:SchemaVersion is required.");
            return errors;
        }
    }
}
