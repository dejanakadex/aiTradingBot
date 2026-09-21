using System;

namespace TradingBot.Persistence
{
    public class AiAnalysisRecord
    {
        public int Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string AgentType { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public string ResultJson { get; set; } = string.Empty;
    }
}
