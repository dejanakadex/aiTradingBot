using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;

namespace TradingBot.Web.HealthChecks
{
    public sealed class OpenAiAvailabilityHealthCheck : IHealthCheck
    {
        private readonly OpenAiSettings _settings;
        private readonly IOpenAiApiKeyProvider _apiKeyProvider;

        public OpenAiAvailabilityHealthCheck(IOptions<OpenAiSettings> settings, IOpenAiApiKeyProvider apiKeyProvider)
        {
            _settings = settings.Value;
            _apiKeyProvider = apiKeyProvider;
        }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!Uri.TryCreate(_settings.ResponsesEndpoint, UriKind.Absolute, out _))
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("OpenAI endpoint configuration is invalid."));
            }

            if (string.IsNullOrWhiteSpace(_settings.Analyzer.Model)
                || string.IsNullOrWhiteSpace(_settings.Analyzer.PromptVersion)
                || string.IsNullOrWhiteSpace(_settings.Critic.Model)
                || string.IsNullOrWhiteSpace(_settings.Critic.PromptVersion))
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("OpenAI analyzer/critic configuration is incomplete."));
            }

            var apiKeyResolution = _apiKeyProvider.Resolve();

            return Task.FromResult(!apiKeyResolution.IsConfigured
                ? HealthCheckResult.Unhealthy(apiKeyResolution.FailureReason)
                : HealthCheckResult.Healthy("OpenAI configuration is present."));
        }
    }
}
