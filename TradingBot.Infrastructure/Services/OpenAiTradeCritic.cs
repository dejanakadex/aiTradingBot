using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class OpenAiTradeCritic : IAiTradeCritic
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly HttpClient _httpClient;
        private readonly OpenAiSettings _settings;
        private readonly IOpenAiApiKeyProvider _apiKeyProvider;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IAiUsageLimiter _usageLimiter;
        private readonly ILogger<OpenAiTradeCritic> _logger;

        public OpenAiTradeCritic(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<OpenAiTradeCritic> logger)
            : this(httpClient, settings, dbFactory, new NoopAiUsageLimiter(), new OpenAiApiKeyProvider(settings), logger)
        {
        }

        public OpenAiTradeCritic(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IAiUsageLimiter usageLimiter,
            ILogger<OpenAiTradeCritic> logger)
            : this(httpClient, settings, dbFactory, usageLimiter, new OpenAiApiKeyProvider(settings), logger)
        {
        }

        public OpenAiTradeCritic(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IAiUsageLimiter usageLimiter,
            IOpenAiApiKeyProvider apiKeyProvider,
            ILogger<OpenAiTradeCritic> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _settings = settings?.Value ?? new OpenAiSettings();
            _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _usageLimiter = usageLimiter ?? throw new ArgumentNullException(nameof(usageLimiter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AiTradeCriticResult> CritiqueAsync(
            MarketSnapshot snapshot,
            PatternCandidate pattern,
            AiMarketAnalysisResult analysis,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (pattern == null) throw new ArgumentNullException(nameof(pattern));
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var requestPayload = BuildRequestPayload(snapshot, pattern, analysis);
            var contextJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
            var apiKeyResolution = _apiKeyProvider.Resolve();
            var apiKey = apiKeyResolution.ApiKey;
            var patternName = pattern.PatternType.ToString();

            var usageDecision = await _usageLimiter.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!usageDecision.Approved)
            {
                var fallback = AiTradeCriticResult.Reject($"REJECT: OpenAI usage limit reached. {usageDecision.Reason}", CriticModel);
                await TryPersistAsync(contextJson, fallback, cancellationToken).ConfigureAwait(false);
                return fallback;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var reason = string.IsNullOrWhiteSpace(apiKeyResolution.FailureReason)
                    ? "OpenAI API key is not configured."
                    : apiKeyResolution.FailureReason;
                var fallback = AiTradeCriticResult.Reject($"REJECT: {reason}", CriticModel);
                await TryPersistAsync(contextJson, fallback, cancellationToken).ConfigureAwait(false);
                return fallback;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var responseJson = await SendWithRetryAsync(contextJson, apiKey, cancellationToken).ConfigureAwait(false);
                var usage = ParseTokenUsage(responseJson);
                var result = ParseStructuredResult(responseJson);
                var valid = IsValid(result);
                if (!valid)
                {
                    result = AiTradeCriticResult.Reject("REJECT: OpenAI returned invalid critic output.", CriticModel);
                }

                await PersistUsageAsync(
                    "TradeCritic",
                    snapshot.Symbol,
                    patternName,
                    stopwatch.ElapsedMilliseconds,
                    usage,
                    valid,
                    valid ? string.Empty : "Invalid structured critic output.",
                    CriticPrompt,
                    CriticModel,
                    cancellationToken).ConfigureAwait(false);

                if (!await TryPersistAsync(contextJson, result, cancellationToken).ConfigureAwait(false))
                {
                    return AiTradeCriticResult.Reject("REJECT: AI critic result could not be persisted.", CriticModel);
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                var failureReason = OpenAiErrorSanitizer.BuildFailureReason(ex);
                var fallback = AiTradeCriticResult.Reject($"REJECT: AI trade critic unavailable ({failureReason}).", CriticModel);
                _logger.LogWarning(ex, "AI trade critic unavailable for {Symbol}", snapshot.Symbol);
                await PersistUsageAsync(
                    "TradeCritic",
                    snapshot.Symbol,
                    patternName,
                    stopwatch.ElapsedMilliseconds,
                    null,
                    false,
                    failureReason,
                    CriticPrompt,
                    CriticModel,
                    CancellationToken.None).ConfigureAwait(false);
                await TryPersistAsync(contextJson, fallback, CancellationToken.None).ConfigureAwait(false);
                return fallback;
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation("AI trade critic request for {Symbol} completed in {ElapsedMs} ms", snapshot.Symbol, stopwatch.ElapsedMilliseconds);
            }
        }

        private string CriticModel => _settings.Critic.Model;

        private string CriticPrompt => _settings.Critic.PromptVersion;

        private async Task<string> SendWithRetryAsync(string contextJson, string apiKey, CancellationToken cancellationToken)
        {
            var maxAttempts = Math.Max(1, _settings.MaxRetries + 1);
            Exception? lastException = null;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)));

                try
                {
                    using var request = BuildHttpRequest(contextJson, apiKey);
                    using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                    var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        return responseBody;
                    }

                    if (!IsTransientStatusCode(response.StatusCode) || attempt == maxAttempts)
                    {
                        throw new OpenAiRequestException(
                            response.StatusCode,
                            OpenAiErrorSanitizer.BuildSafeMessage(response.StatusCode, responseBody));
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastException = new TimeoutException("OpenAI critic request timed out.");
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    lastException = ex;
                }

                if (attempt < maxAttempts)
                {
                    await Task.Delay(250 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("OpenAI critic request failed after retries.", lastException);
        }

        private HttpRequestMessage BuildHttpRequest(string contextJson, string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.ResponsesEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(contextJson, Encoding.UTF8, "application/json");
            return request;
        }

        private object BuildRequestPayload(MarketSnapshot snapshot, PatternCandidate pattern, AiMarketAnalysisResult analysis)
        {
            return new
            {
                model = CriticModel,
                instructions = "You are an analysis-only trade critic. Your job is not to find reasons to trade. Your job is specifically to find reasons why a proposed long trade should be rejected. You do not submit orders, size positions, bypass risk rules, call brokers, or modify strategy/configuration. Return only structured JSON matching the schema.",
                input = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new
                            {
                                type = "input_text",
                                text = JsonSerializer.Serialize(new
                                {
                                    task = "Critique the proposed long-trade analysis and identify rejection reasons. Approve only when rejection risks are not material.",
                                    marketSnapshot = snapshot,
                                    detectedPattern = pattern,
                                    detectedPatternName = pattern.PatternType.ToString(),
                                    detectedPatternSymbol = pattern.Symbol,
                                    detectedPatternDetectedAtUtc = pattern.DetectedAtUtc,
                                    aiMarketAnalysis = analysis,
                                    criticPromptVersion = CriticPrompt,
                                    rejectionExamples = new[]
                                    {
                                        "weakening higher timeframe trend",
                                        "abnormal sell volume",
                                        "false breakout risk",
                                        "poor reward/risk",
                                        "excessive volatility",
                                        "poor liquidity",
                                        "large spread",
                                        "conflicting timeframe signals",
                                        "weak pattern structure"
                                    },
                                    safeDefault = "If the analysis is unavailable, malformed, uncertain, or materially risky, set approved=false."
                                }, JsonOptions)
                            }
                        }
                    }
                },
                text = new
                {
                    format = new
                    {
                        type = "json_schema",
                        name = "ai_trade_critic",
                        strict = true,
                        schema = BuildResponseSchema()
                    }
                }
            };
        }

        private static object BuildResponseSchema()
        {
            return new
            {
                type = "object",
                properties = new
                {
                    approved = new { type = "boolean" },
                    confidence = new { type = "number", minimum = 0, maximum = 1 },
                    riskFlags = new
                    {
                        type = "array",
                        items = new { type = "string" }
                    },
                    reason = new { type = "string" }
                },
                required = new[] { "approved", "confidence", "riskFlags", "reason" },
                additionalProperties = false
            };
        }

        private AiTradeCriticResult ParseStructuredResult(string responseJson)
        {
            using var document = JsonDocument.Parse(responseJson);
            var content = ExtractResponseText(document.RootElement);

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("OpenAI Responses API critic result did not contain structured output text.");
            }

            var result = JsonSerializer.Deserialize<AiTradeCriticResult>(content, JsonOptions)
                ?? throw new InvalidOperationException("OpenAI critic structured content could not be deserialized.");

            return new AiTradeCriticResult
            {
                Approved = result.Approved,
                Confidence = result.Confidence,
                RiskFlags = result.RiskFlags,
                Reason = result.Reason,
                CritiquedAtUtc = DateTime.UtcNow,
                Model = CriticModel,
                IsSafeFallback = false
            };
        }

        private static bool IsValid(AiTradeCriticResult result)
        {
            if (result.Confidence < 0m || result.Confidence > 1m) return false;
            if (result.RiskFlags == null) return false;
            if (string.IsNullOrWhiteSpace(result.Reason)) return false;
            return true;
        }

        private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        {
            return statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)statusCode >= 500;
        }

        private static AiTokenUsage? ParseTokenUsage(string responseBody)
        {
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("usage", out var usage)) return null;

                int? promptTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : null;
                int? completionTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : null;
                int? totalTokens = usage.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : null;
                int? cachedInputTokens = null;
                if (usage.TryGetProperty("input_tokens_details", out var details)
                    && details.TryGetProperty("cached_tokens", out var cached))
                {
                    cachedInputTokens = cached.GetInt32();
                }

                return new AiTokenUsage(promptTokens, completionTokens, totalTokens, cachedInputTokens);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ExtractResponseText(JsonElement root)
        {
            if (root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && !string.Equals(status.GetString(), "completed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"OpenAI Responses API returned non-completed status '{status.GetString()}'.");
            }

            if (root.TryGetProperty("output_text", out var outputText)
                && outputText.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(outputText.GetString()))
            {
                return outputText.GetString();
            }

            if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return null;

            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "output_text", StringComparison.OrdinalIgnoreCase)
                        && part.TryGetProperty("text", out var text)
                        && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString();
                    }
                }
            }

            return null;
        }

        private async Task PersistUsageAsync(
            string requestType,
            string symbol,
            string pattern,
            long durationMs,
            AiTokenUsage? usage,
            bool success,
            string failureReason,
            string promptVersion,
            string model,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                db.AiApiUsageRecords.Add(new TradingBot.Persistence.AiApiUsageRecord
                {
                    AgentType = AiAgentType.Critic.ToString(),
                    RequestType = requestType,
                    Symbol = symbol,
                    Pattern = pattern,
                    TimestampUtc = DateTime.UtcNow,
                    InputTokens = usage?.InputTokens,
                    OutputTokens = usage?.OutputTokens,
                    TotalTokens = usage?.TotalTokens,
                    CachedInputTokens = usage?.CachedInputTokens,
                    DurationMs = durationMs,
                    Model = model,
                    PromptVersion = promptVersion,
                    Success = success,
                    FailureReason = failureReason
                });

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(ex, "Failed to persist OpenAI usage record for {RequestType} {Symbol}", requestType, symbol);
            }

            _logger.LogInformation(
                "OpenAI usage recorded: type={RequestType}, symbol={Symbol}, pattern={Pattern}, inputTokens={InputTokens}, outputTokens={OutputTokens}, totalTokens={TotalTokens}, cachedInputTokens={CachedInputTokens}, durationMs={DurationMs}, success={Success}",
                requestType,
                symbol,
                pattern,
                usage?.InputTokens,
                usage?.OutputTokens,
                usage?.TotalTokens,
                usage?.CachedInputTokens,
                durationMs,
                success);
        }

        private async Task PersistAsync(string contextJson, AiTradeCriticResult result, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.AiAnalysisRecords.Add(new TradingBot.Persistence.AiAnalysisRecord
            {
                TimestampUtc = DateTime.UtcNow,
                AgentType = AiAgentType.Critic.ToString(),
                Model = CriticModel,
                PromptVersion = CriticPrompt,
                Context = contextJson,
                ResultJson = JsonSerializer.Serialize(new
                {
                    agentType = AiAgentType.Critic.ToString(),
                    model = CriticModel,
                    promptVersion = CriticPrompt,
                    result
                }, JsonOptions)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryPersistAsync(string contextJson, AiTradeCriticResult result, CancellationToken cancellationToken)
        {
            try
            {
                await PersistAsync(contextJson, result, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(ex, "Failed to persist AI trade critic result for model {Model}; downstream trading must fail closed.", CriticModel);
                return false;
            }
        }

        private sealed record AiTokenUsage(int? InputTokens, int? OutputTokens, int? TotalTokens, int? CachedInputTokens);
    }
}
