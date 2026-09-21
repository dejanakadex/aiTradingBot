using System;
using System.Diagnostics;
using System.Linq;
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
    public sealed class OpenAiMarketAnalyzer : IAiMarketAnalyzer
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };

        private readonly HttpClient _httpClient;
        private readonly OpenAiSettings _settings;
        private readonly IOpenAiApiKeyProvider _apiKeyProvider;
        private readonly IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> _dbFactory;
        private readonly IAiAnalysisValidator _validator;
        private readonly IAiUsageLimiter _usageLimiter;
        private readonly ILogger<OpenAiMarketAnalyzer> _logger;

        public OpenAiMarketAnalyzer(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            ILogger<OpenAiMarketAnalyzer> logger)
            : this(
                httpClient,
                settings,
                dbFactory,
                new AiAnalysisValidator(Microsoft.Extensions.Options.Options.Create(new TradingSettings())),
                new NoopAiUsageLimiter(),
                new OpenAiApiKeyProvider(settings),
                logger)
        {
        }

        public OpenAiMarketAnalyzer(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IAiAnalysisValidator validator,
            IAiUsageLimiter usageLimiter,
            ILogger<OpenAiMarketAnalyzer> logger)
            : this(httpClient, settings, dbFactory, validator, usageLimiter, new OpenAiApiKeyProvider(settings), logger)
        {
        }

        public OpenAiMarketAnalyzer(
            HttpClient httpClient,
            IOptions<OpenAiSettings> settings,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> dbFactory,
            IAiAnalysisValidator validator,
            IAiUsageLimiter usageLimiter,
            IOpenAiApiKeyProvider apiKeyProvider,
            ILogger<OpenAiMarketAnalyzer> logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _settings = settings?.Value ?? new OpenAiSettings();
            _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _usageLimiter = usageLimiter ?? throw new ArgumentNullException(nameof(usageLimiter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<AiMarketAnalysisResult> AnalyzeAsync(
            MarketSnapshot snapshot,
            PatternCandidate? detectedPattern = null,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var requestPayload = BuildRequestPayload(snapshot, detectedPattern);
            var contextJson = JsonSerializer.Serialize(requestPayload, JsonOptions);
            var apiKeyResolution = _apiKeyProvider.Resolve();
            var apiKey = apiKeyResolution.ApiKey;
            var patternName = detectedPattern?.PatternType.ToString() ?? "Unknown";

            var usageDecision = await _usageLimiter.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!usageDecision.Approved)
            {
                var fallback = AiMarketAnalysisResult.NoTrade($"NO TRADE: OpenAI usage limit reached. {usageDecision.Reason}", AnalyzerModel);
                await TryPersistAsync(contextJson, fallback, cancellationToken).ConfigureAwait(false);
                return fallback;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var reason = string.IsNullOrWhiteSpace(apiKeyResolution.FailureReason)
                    ? "OpenAI API key is not configured."
                    : apiKeyResolution.FailureReason;
                var fallback = AiMarketAnalysisResult.NoTrade($"NO TRADE: {reason}", AnalyzerModel);
                await TryPersistAsync(contextJson, fallback, cancellationToken).ConfigureAwait(false);
                return fallback;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var responseJson = await SendWithRetryAsync(contextJson, apiKey, cancellationToken).ConfigureAwait(false);
                var usage = ParseTokenUsage(responseJson);
                var result = ParseStructuredResult(responseJson);
                var validationReason = string.Empty;
                var valid = IsValid(result) && _validator.TryValidate(snapshot, result, out validationReason);
                if (!valid)
                {
                    result = AiMarketAnalysisResult.NoTrade($"NO TRADE: OpenAI returned invalid structured analysis. {validationReason}", AnalyzerModel);
                }

                await PersistUsageAsync(
                    "MarketAnalyzer",
                    snapshot.Symbol,
                    patternName,
                    stopwatch.ElapsedMilliseconds,
                    usage,
                    valid,
                    valid ? string.Empty : "Invalid structured analyzer output.",
                    AnalyzerPrompt,
                    AnalyzerModel,
                    cancellationToken).ConfigureAwait(false);

                if (!await TryPersistAsync(contextJson, result, cancellationToken).ConfigureAwait(false))
                {
                    return AiMarketAnalysisResult.NoTrade("NO TRADE: AI analysis could not be persisted.", AnalyzerModel);
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
                var fallback = AiMarketAnalysisResult.NoTrade($"NO TRADE: AI analysis unavailable ({failureReason}).", AnalyzerModel);
                _logger.LogWarning(ex, "AI market analysis unavailable for {Symbol}", snapshot.Symbol);
                await PersistUsageAsync(
                    "MarketAnalyzer",
                    snapshot.Symbol,
                    patternName,
                    stopwatch.ElapsedMilliseconds,
                    null,
                    false,
                    failureReason,
                    AnalyzerPrompt,
                    AnalyzerModel,
                    CancellationToken.None).ConfigureAwait(false);
                await TryPersistAsync(contextJson, fallback, CancellationToken.None).ConfigureAwait(false);
                return fallback;
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogInformation("AI market analysis request for {Symbol} completed in {ElapsedMs} ms", snapshot.Symbol, stopwatch.ElapsedMilliseconds);
            }
        }

        private string AnalyzerModel => _settings.Analyzer.Model;

        private string AnalyzerPrompt => _settings.Analyzer.PromptVersion;

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
                    lastException = new TimeoutException("OpenAI request timed out.");
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    lastException = ex;
                }

                if (attempt < maxAttempts)
                {
                    var delayMs = 250 * attempt;
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("OpenAI request failed after retries.", lastException);
        }

        private HttpRequestMessage BuildHttpRequest(string contextJson, string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.ResponsesEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(contextJson, Encoding.UTF8, "application/json");
            return request;
        }

        private object BuildRequestPayload(MarketSnapshot snapshot, PatternCandidate? detectedPattern)
        {
            var primaryPattern = detectedPattern
                ?? snapshot.OneMinute.DetectedPatterns.FirstOrDefault()
                ?? snapshot.FiveMinutes.DetectedPatterns.FirstOrDefault()
                ?? snapshot.FifteenMinutes.DetectedPatterns.FirstOrDefault();

            return new
            {
                model = AnalyzerModel,
                instructions = "You are an analysis-only market context evaluator. You do not call brokers, submit orders, determine final position size, bypass risk rules, modify configuration, or modify strategy code. Return only structured JSON matching the schema.",
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
                                    task = "Evaluate whether the detected pattern is valid in context, whether the move looks like a temporary pullback or more serious decline, and whether the setup is strong enough to consider a long trade. This is analysis only; downstream strategy and risk services make final decisions.",
                                    marketSnapshot = snapshot,
                                    detectedPattern = primaryPattern,
                                    detectedPatternName = primaryPattern?.PatternType.ToString() ?? "Unknown",
                                    detectedPatternSymbol = primaryPattern?.Symbol ?? snapshot.Symbol,
                                    detectedPatternDetectedAtUtc = primaryPattern?.DetectedAtUtc,
                                    analyzerPromptVersion = AnalyzerPrompt,
                                    timeframeInterpretation = new
                                    {
                                        fifteenMinutes = "broader market direction",
                                        fiveMinutes = "trading setup / pullback context",
                                        oneMinute = "entry timing"
                                    },
                                    requiredContext = new[]
                                    {
                                        "recent candles",
                                        "technical indicators",
                                        "volume",
                                        "volatility",
                                        "VWAP",
                                        "support/resistance",
                                        "spread if available"
                                    },
                                    safeDefault = "If context is insufficient or uncertain, return REJECT or WAIT. Never imply a guaranteed trade."
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
                        name = "ai_market_analysis",
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
                    action = new { type = "string", @enum = new[] { AiMarketActions.Buy, AiMarketActions.Wait, AiMarketActions.Reject } },
                    confidence = new { type = "number", minimum = 0, maximum = 1 },
                    patternQuality = new { type = "number", minimum = 0, maximum = 1 },
                    marketRegime = new { type = "string" },
                    expectedMovePercent = new { type = "number" },
                    expectedHorizonMinutes = new { type = "integer", minimum = 0 },
                    entryMin = new { type = "number" },
                    entryMax = new { type = "number" },
                    invalidationPrice = new { type = "number" },
                    reason = new { type = "string" }
                },
                required = new[]
                {
                    "action",
                    "confidence",
                    "patternQuality",
                    "marketRegime",
                    "expectedMovePercent",
                    "expectedHorizonMinutes",
                    "entryMin",
                    "entryMax",
                    "invalidationPrice",
                    "reason"
                },
                additionalProperties = false
            };
        }

        private AiMarketAnalysisResult ParseStructuredResult(string responseJson)
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var content = ExtractResponseText(root);

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("OpenAI Responses API result did not contain structured output text.");
            }

            var result = JsonSerializer.Deserialize<AiMarketAnalysisResult>(content, JsonOptions)
                ?? throw new InvalidOperationException("OpenAI structured content could not be deserialized.");

            return new AiMarketAnalysisResult
            {
                Action = result.Action,
                Confidence = result.Confidence,
                PatternQuality = result.PatternQuality,
                MarketRegime = result.MarketRegime,
                ExpectedMovePercent = result.ExpectedMovePercent,
                ExpectedHorizonMinutes = result.ExpectedHorizonMinutes,
                EntryMin = result.EntryMin,
                EntryMax = result.EntryMax,
                InvalidationPrice = result.InvalidationPrice,
                Reason = result.Reason,
                AnalyzedAtUtc = DateTime.UtcNow,
                Model = AnalyzerModel,
                IsSafeFallback = false
            };
        }

        private static bool IsValid(AiMarketAnalysisResult result)
        {
            if (result.Action is not (AiMarketActions.Buy or AiMarketActions.Wait or AiMarketActions.Reject)) return false;
            if (result.Confidence < 0m || result.Confidence > 1m) return false;
            if (result.PatternQuality < 0m || result.PatternQuality > 1m) return false;
            if (result.ExpectedHorizonMinutes < 0) return false;
            if (result.EntryMin > result.EntryMax) return false;
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
                    AgentType = AiAgentType.Analyzer.ToString(),
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

        private async Task PersistAsync(string contextJson, AiMarketAnalysisResult result, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.AiAnalysisRecords.Add(new TradingBot.Persistence.AiAnalysisRecord
            {
                TimestampUtc = DateTime.UtcNow,
                AgentType = AiAgentType.Analyzer.ToString(),
                Model = AnalyzerModel,
                PromptVersion = AnalyzerPrompt,
                Context = contextJson,
                ResultJson = JsonSerializer.Serialize(new
                {
                    agentType = AiAgentType.Analyzer.ToString(),
                    model = AnalyzerModel,
                    promptVersion = AnalyzerPrompt,
                    result
                }, JsonOptions)
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryPersistAsync(string contextJson, AiMarketAnalysisResult result, CancellationToken cancellationToken)
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
                _logger.LogError(ex, "Failed to persist AI market analysis for model {Model}; downstream trading must fail closed.", AnalyzerModel);
                return false;
            }
        }

        private sealed record AiTokenUsage(int? InputTokens, int? OutputTokens, int? TotalTokens, int? CachedInputTokens);
    }
}
