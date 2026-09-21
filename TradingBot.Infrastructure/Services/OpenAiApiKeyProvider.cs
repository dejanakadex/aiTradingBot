using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class OpenAiApiKeyProvider : IOpenAiApiKeyProvider
    {
        private readonly OpenAiSettings _settings;

        public OpenAiApiKeyProvider(IOptions<OpenAiSettings> settings)
        {
            _settings = settings?.Value ?? new OpenAiSettings();
        }

        public OpenAiApiKeyResolution Resolve()
        {
            var directKey = Normalize(_settings.ApiKey);
            if (!string.IsNullOrWhiteSpace(directKey) && LooksLikeOpenAiApiKey(directKey))
            {
                return Configured(directKey, "OpenAiSettings:ApiKey");
            }

            var apiKeyName = Normalize(_settings.ApiKeyName);
            if (string.IsNullOrWhiteSpace(apiKeyName))
            {
                return NotConfigured("OpenAiSettings:ApiKey or OpenAiSettings:ApiKeyName is not configured.");
            }

            if (LooksLikeOpenAiApiKey(apiKeyName))
            {
                return Configured(apiKeyName, "OpenAiSettings:ApiKeyName direct value");
            }

            var environmentKey = Normalize(Environment.GetEnvironmentVariable(apiKeyName));
            if (!string.IsNullOrWhiteSpace(environmentKey))
            {
                return Configured(environmentKey, $"Environment:{apiKeyName}");
            }

            return NotConfigured($"{apiKeyName} environment variable is not configured.");
        }

        private static OpenAiApiKeyResolution Configured(string apiKey, string source)
        {
            return new OpenAiApiKeyResolution(true, apiKey, source, string.Empty);
        }

        private static OpenAiApiKeyResolution NotConfigured(string reason)
        {
            return new OpenAiApiKeyResolution(false, null, string.Empty, reason);
        }

        private static string Normalize(string? value)
        {
            return value?.Trim() ?? string.Empty;
        }

        private static bool LooksLikeOpenAiApiKey(string value)
        {
            return value.StartsWith("sk-", StringComparison.Ordinal)
                || value.StartsWith("sess-", StringComparison.Ordinal);
        }
    }
}
