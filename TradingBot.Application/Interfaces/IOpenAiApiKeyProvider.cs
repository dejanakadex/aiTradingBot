namespace TradingBot.Application.Interfaces
{
    public interface IOpenAiApiKeyProvider
    {
        OpenAiApiKeyResolution Resolve();
    }

    public sealed record OpenAiApiKeyResolution(
        bool IsConfigured,
        string? ApiKey,
        string Source,
        string FailureReason);
}
