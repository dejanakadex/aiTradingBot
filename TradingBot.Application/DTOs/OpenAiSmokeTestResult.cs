namespace TradingBot.Application.DTOs
{
    public sealed class OpenAiSmokeTestResult
    {
        public bool Success { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public int? HttpStatus { get; init; }
        public OpenAiSmokeTestStageResult Analyzer { get; init; } = new();
        public OpenAiSmokeTestStageResult Critic { get; init; } = new();
        public int TotalTokens { get; init; }
    }

    public sealed class OpenAiSmokeTestStageResult
    {
        public bool Success { get; init; }
        public string Model { get; init; } = string.Empty;
        public string PromptVersion { get; init; } = string.Empty;
        public string? Action { get; init; }
        public bool? Approved { get; init; }
        public decimal? Confidence { get; init; }
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }
        public int? TotalTokens { get; init; }
        public int? CachedInputTokens { get; init; }
        public long DurationMs { get; init; }
        public string Error { get; init; } = string.Empty;
        public int? HttpStatus { get; init; }
    }
}
