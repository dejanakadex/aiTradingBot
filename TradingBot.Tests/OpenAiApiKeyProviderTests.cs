using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class OpenAiApiKeyProviderTests
    {
        [Fact]
        public void Resolve_UsesDirectApiKeyWhenConfigured()
        {
            var provider = CreateProvider(new OpenAiSettings
            {
                ApiKey = " sk-test-direct ",
                ApiKeyName = "MISSING_DIRECT_SHOULD_WIN"
            });

            var result = provider.Resolve();

            Assert.True(result.IsConfigured);
            Assert.Equal("sk-test-direct", result.ApiKey);
            Assert.Equal("OpenAiSettings:ApiKey", result.Source);
            Assert.Equal(string.Empty, result.FailureReason);
        }

        [Fact]
        public void Resolve_UsesEnvironmentVariableNamedByApiKeyName()
        {
            const string apiKeyName = "TRADINGBOT_PROVIDER_TEST_KEY";
            Environment.SetEnvironmentVariable(apiKeyName, "sk-test-env");
            var provider = CreateProvider(new OpenAiSettings { ApiKeyName = apiKeyName });

            var result = provider.Resolve();

            Assert.True(result.IsConfigured);
            Assert.Equal("sk-test-env", result.ApiKey);
            Assert.Equal($"Environment:{apiKeyName}", result.Source);

            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public void Resolve_IgnoresApiKeyPlaceholderAndUsesEnvironmentVariable()
        {
            const string apiKeyName = "TRADINGBOT_PROVIDER_PLACEHOLDER_KEY";
            Environment.SetEnvironmentVariable(apiKeyName, "sk-env");
            var provider = CreateProvider(new OpenAiSettings
            {
                ApiKey = apiKeyName,
                ApiKeyName = apiKeyName
            });

            var result = provider.Resolve();

            Assert.True(result.IsConfigured);
            Assert.Equal("sk-env", result.ApiKey);
            Assert.Equal($"Environment:{apiKeyName}", result.Source);

            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public void Resolve_IgnoresNonKeyDirectApiKeyValueWhenEnvironmentVariableIsMissing()
        {
            const string apiKeyName = "TRADINGBOT_PROVIDER_MISSING_PLACEHOLDER_KEY";
            Environment.SetEnvironmentVariable(apiKeyName, null);
            var provider = CreateProvider(new OpenAiSettings
            {
                ApiKey = apiKeyName,
                ApiKeyName = apiKeyName
            });

            var result = provider.Resolve();

            Assert.False(result.IsConfigured);
            Assert.Contains(apiKeyName, result.FailureReason);
        }

        [Fact]
        public void Resolve_TreatsOpenAiKeyInApiKeyNameAsDirectValueForCompatibility()
        {
            var provider = CreateProvider(new OpenAiSettings { ApiKeyName = "sk-test-compat" });

            var result = provider.Resolve();

            Assert.True(result.IsConfigured);
            Assert.Equal("sk-test-compat", result.ApiKey);
            Assert.Equal("OpenAiSettings:ApiKeyName direct value", result.Source);
        }

        [Fact]
        public void Resolve_ReturnsFailureWhenEnvironmentVariableIsMissing()
        {
            const string apiKeyName = "TRADINGBOT_PROVIDER_MISSING_KEY";
            Environment.SetEnvironmentVariable(apiKeyName, null);
            var provider = CreateProvider(new OpenAiSettings { ApiKeyName = apiKeyName });

            var result = provider.Resolve();

            Assert.False(result.IsConfigured);
            Assert.Null(result.ApiKey);
            Assert.Contains(apiKeyName, result.FailureReason);
        }

        private static OpenAiApiKeyProvider CreateProvider(OpenAiSettings settings)
        {
            return new OpenAiApiKeyProvider(Options.Create(settings));
        }
    }
}
