using System;

namespace TradingBot.Persistence
{
    public sealed class AiApiUsageRecord
    {
        public int Id { get; set; }
        public string AgentType { get; set; } = string.Empty;
        public string RequestType { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? TotalTokens { get; set; }
        public int? CachedInputTokens { get; set; }
        public long DurationMs { get; set; }
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string FailureReason { get; set; } = string.Empty;
    }
}
