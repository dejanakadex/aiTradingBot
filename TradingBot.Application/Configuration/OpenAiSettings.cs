namespace TradingBot.Application.Configuration
{
    public class OpenAiSettings
    {
        public string ApiKey { get; set; } = string.Empty; // optional direct key; prefer ApiKeyName + environment variable
        public string ApiKeyName { get; set; } = "OPENAI_API_KEY"; // name of env var or secret
        public AiAgentSettings Analyzer { get; set; } = new() { Model = "gpt-4o-mini", PromptVersion = "analyzer-v1" };
        public AiAgentSettings Critic { get; set; } = new() { Model = "gpt-4o-mini", PromptVersion = "critic-v1" };
        public string ResponsesEndpoint { get; set; } = "https://api.openai.com/v1/responses";
        public int TimeoutSeconds { get; set; } = 30;
        public int MaxRetries { get; set; } = 2;
        public int MaximumAiCallsPerMinute { get; set; } = 20;
        public int MaximumAiCallsPerDay { get; set; } = 500;
        public int MaximumAiInputTokensPerDay { get; set; } = 500000;
        public int MaximumAiOutputTokensPerDay { get; set; } = 100000;
        public OpenAiMarketContextSettings MarketContext { get; set; } = new();
        public OpenAiPricingSettings Pricing { get; set; } = new();
    }

    public class AiAgentSettings
    {
        public string Model { get; set; } = string.Empty;
        public string PromptVersion { get; set; } = string.Empty;
    }

    public class OpenAiMarketContextSettings
    {
        public int OneMinuteCandles { get; set; } = 60;
        public int FiveMinuteCandles { get; set; } = 50;
        public int FifteenMinuteCandles { get; set; } = 40;
    }

    public class OpenAiPricingSettings
    {
        public OpenAiAgentPricingSettings Analyzer { get; set; } = new();
        public OpenAiAgentPricingSettings Critic { get; set; } = new();
    }

    public class OpenAiAgentPricingSettings
    {
        public decimal InputPricePerMillionTokens { get; set; }
        public decimal OutputPricePerMillionTokens { get; set; }
    }
}
