using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using AiApiUsageRecord = TradingBot.Persistence.AiApiUsageRecord;
using TradingBotDbContext = TradingBot.Persistence.TradingBotDbContext;

namespace TradingBot.Infrastructure.Services
{
    public sealed class OpenAiSmokeTestService : IOpenAiSmokeTestService
    {
        private const string SmokeSymbol = "TEST";
        private const string SmokeRequestType = "SmokeTest";
        private const string SmokeAnalyzerAgentType = "SmokeTestAnalyzer";
        private const string SmokeCriticAgentType = "SmokeTestCritic";

        private readonly IAiMarketAnalyzer _analyzer;
        private readonly IAiTradeCritic _critic;
        private readonly IAiAnalysisValidator _validator;
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IOpenAiApiKeyProvider _apiKeyProvider;
        private readonly OpenAiSettings _settings;
        private readonly ILogger<OpenAiSmokeTestService> _logger;

        public OpenAiSmokeTestService(
            IAiMarketAnalyzer analyzer,
            IAiTradeCritic critic,
            IAiAnalysisValidator validator,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IOpenAiApiKeyProvider apiKeyProvider,
            IOptions<OpenAiSettings> settings,
            ILogger<OpenAiSmokeTestService> logger)
        {
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            _critic = critic ?? throw new ArgumentNullException(nameof(critic));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
            _settings = settings?.Value ?? new OpenAiSettings();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<OpenAiSmokeTestResult> RunAsync(CancellationToken cancellationToken = default)
        {
            var analyzerStage = NewAnalyzerStage();
            var criticStage = NewCriticStage();
            var configurationError = ValidateConfiguration();
            if (!string.IsNullOrWhiteSpace(configurationError))
            {
                return Failure("Configuration", configurationError, analyzerStage, criticStage);
            }

            var snapshot = BuildSyntheticSnapshot();
            var pattern = BuildSyntheticPattern();

            try
            {
                var analyzerStartedUtc = DateTime.UtcNow;
                var analyzerResult = await _analyzer.AnalyzeAsync(snapshot, pattern, cancellationToken).ConfigureAwait(false);
                var analyzerUsage = await MarkSmokeRecordsAsync(
                    nameof(AiAgentType.Analyzer),
                    SmokeAnalyzerAgentType,
                    _settings.Analyzer.Model,
                    _settings.Analyzer.PromptVersion,
                    analyzerStartedUtc,
                    cancellationToken).ConfigureAwait(false);

                analyzerStage = BuildAnalyzerStage(analyzerResult, analyzerUsage);
                if (!IsAnalyzerSmokeResultValid(snapshot, analyzerResult, out var analyzerValidationReason))
                {
                    analyzerStage = analyzerStage.WithFailure(analyzerValidationReason, ExtractHttpStatus(analyzerUsage?.FailureReason));
                    return Failure("Analyzer", analyzerValidationReason, analyzerStage, criticStage, analyzerStage.HttpStatus);
                }
                if (analyzerStage.TotalTokens is null)
                {
                    const string analyzerUsageError = "Analyzer token usage was not captured.";
                    analyzerStage = analyzerStage.WithFailure(analyzerUsageError, null);
                    return Failure("Analyzer", analyzerUsageError, analyzerStage, criticStage);
                }

                var criticStartedUtc = DateTime.UtcNow;
                var criticResult = await _critic.CritiqueAsync(snapshot, pattern, BuildCriticSmokeAnalysis(), cancellationToken).ConfigureAwait(false);
                var criticUsage = await MarkSmokeRecordsAsync(
                    nameof(AiAgentType.Critic),
                    SmokeCriticAgentType,
                    _settings.Critic.Model,
                    _settings.Critic.PromptVersion,
                    criticStartedUtc,
                    cancellationToken).ConfigureAwait(false);

                criticStage = BuildCriticStage(criticResult, criticUsage);
                if (!IsCriticSmokeResultValid(criticResult, out var criticValidationReason))
                {
                    criticStage = criticStage.WithFailure(criticValidationReason, ExtractHttpStatus(criticUsage?.FailureReason));
                    return Failure("Critic", criticValidationReason, analyzerStage, criticStage, criticStage.HttpStatus);
                }
                if (criticStage.TotalTokens is null)
                {
                    const string criticUsageError = "Critic token usage was not captured.";
                    criticStage = criticStage.WithFailure(criticUsageError, null);
                    return Failure("Critic", criticUsageError, analyzerStage, criticStage);
                }

                return new OpenAiSmokeTestResult
                {
                    Success = true,
                    Stage = "Completed",
                    Analyzer = analyzerStage,
                    Critic = criticStage,
                    TotalTokens = analyzerStage.TotalTokensOrZero() + criticStage.TotalTokensOrZero()
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                _logger.LogError(ex, "OpenAI smoke test failed.");
                return Failure("SmokeTest", "OpenAI smoke test failed before completion. See logs for details.", analyzerStage, criticStage);
            }
        }

        private string ValidateConfiguration()
        {
            var apiKeyResolution = _apiKeyProvider.Resolve();
            if (!apiKeyResolution.IsConfigured)
            {
                return apiKeyResolution.FailureReason;
            }

            if (string.IsNullOrWhiteSpace(_settings.Analyzer.Model))
            {
                return "OpenAiSettings:Analyzer:Model is not configured.";
            }

            if (string.IsNullOrWhiteSpace(_settings.Analyzer.PromptVersion))
            {
                return "OpenAiSettings:Analyzer:PromptVersion is not configured.";
            }

            if (string.IsNullOrWhiteSpace(_settings.Critic.Model))
            {
                return "OpenAiSettings:Critic:Model is not configured.";
            }

            if (string.IsNullOrWhiteSpace(_settings.Critic.PromptVersion))
            {
                return "OpenAiSettings:Critic:PromptVersion is not configured.";
            }

            if (!Uri.TryCreate(_settings.ResponsesEndpoint, UriKind.Absolute, out _))
            {
                return "OpenAiSettings:ResponsesEndpoint is invalid.";
            }

            return string.Empty;
        }

        private static MarketSnapshot BuildSyntheticSnapshot()
        {
            return new MarketSnapshot
            {
                Symbol = SmokeSymbol,
                CreatedAtUtc = DateTime.UtcNow,
                CurrentPrice = 100.85m,
                Spread = 0.02m,
                OneMinute = BuildTimeframe(Timeframe.OneMinute, "Entry timing - Double Bottom forming after VWAP reclaim", 100.10m, 12, 1),
                FiveMinutes = BuildTimeframe(Timeframe.FiveMinutes, "Trading setup - orderly pullback in uptrend", 99.80m, 10, 5),
                FifteenMinutes = BuildTimeframe(Timeframe.FifteenMinutes, "Broader direction - uptrend", 98.50m, 8, 15)
            };
        }

        private static MarketTimeframeSnapshot BuildTimeframe(Timeframe timeframe, string interpretation, decimal basePrice, int count, int minuteStep)
        {
            var now = DateTime.UtcNow;
            var candles = Enumerable.Range(0, count)
                .Select(i =>
                {
                    var price = basePrice + (i * 0.12m);
                    var pullback = timeframe == Timeframe.OneMinute && i is 7 or 9 ? 0.28m : 0m;
                    var close = price - pullback;
                    return new Candle(
                        SmokeSymbol,
                        timeframe,
                        now.AddMinutes(-(count - i) * minuteStep),
                        price - 0.05m,
                        price + 0.22m,
                        close - 0.18m,
                        close,
                        1200m + (i * 35m));
                })
                .ToList();

            var latest = candles[^1];
            var averageVolume = candles.Average(c => c.Volume);
            return new MarketTimeframeSnapshot
            {
                Timeframe = timeframe,
                Interpretation = interpretation,
                RecentCandles = candles,
                Features = new MarketFeatures
                {
                    Symbol = SmokeSymbol,
                    TimestampUtc = latest.TimestampUtc,
                    EmaShort = latest.Close - 0.05m,
                    EmaLong = latest.Close - 0.55m,
                    Rsi = timeframe == Timeframe.OneMinute ? 34m : 48m,
                    Atr = 0.42m,
                    Vwap = latest.Close - 0.20m,
                    VolumeAverage = averageVolume,
                    VolumeRatio = timeframe == Timeframe.OneMinute ? 1.4m : 1.15m,
                    PriceChangePercent = 0.35m,
                    DistanceFromVwapPercent = 0.2m,
                    RecentHigh = candles.Max(c => c.High),
                    RecentLow = candles.Min(c => c.Low),
                    TrendDirection = 1,
                    Volatility = 0.012m
                },
                DetectedPatterns = timeframe == Timeframe.OneMinute
                    ? new[] { BuildSyntheticPattern() }
                    : Array.Empty<PatternCandidate>(),
                SupportResistanceCandidates = new[]
                {
                    new SupportResistanceCandidate { Kind = "Support", Price = candles.Min(c => c.Low), Source = "synthetic-smoke-test" },
                    new SupportResistanceCandidate { Kind = "Resistance", Price = candles.Max(c => c.High), Source = "synthetic-smoke-test" }
                },
                Trend = new TrendInformation
                {
                    Direction = 1,
                    Label = timeframe == Timeframe.FiveMinutes ? "Pullback in uptrend" : "Uptrend",
                    EmaShort = latest.Close - 0.05m,
                    EmaLong = latest.Close - 0.55m,
                    PriceChangePercent = 0.35m
                },
                Volume = new VolumeContext
                {
                    LatestVolume = latest.Volume,
                    AverageVolume = averageVolume,
                    VolumeRatio = timeframe == Timeframe.OneMinute ? 1.4m : 1.15m
                },
                Volatility = new VolatilityContext
                {
                    Atr = 0.42m,
                    Volatility = 0.012m,
                    RecentHigh = candles.Max(c => c.High),
                    RecentLow = candles.Min(c => c.Low)
                }
            };
        }

        private static PatternCandidate BuildSyntheticPattern()
        {
            return new PatternCandidate(
                PatternType.DoubleBottom,
                SmokeSymbol,
                Timeframe.OneMinute,
                DateTime.UtcNow,
                0.82m,
                new[] { 100.35m, 100.40m, 100.95m },
                new Dictionary<string, string>
                {
                    ["context"] = "Synthetic OpenAI smoke test only",
                    ["vwapReclaim"] = "true",
                    ["volumeRatio"] = "1.4",
                    ["smokeTest"] = "true"
                });
        }

        private static AiMarketAnalysisResult BuildCriticSmokeAnalysis()
        {
            return new AiMarketAnalysisResult
            {
                Action = AiMarketActions.Buy,
                Confidence = 0.78m,
                PatternQuality = 0.82m,
                MarketRegime = "Pullback in uptrend above VWAP",
                ExpectedMovePercent = 0.75m,
                ExpectedHorizonMinutes = 20,
                EntryMin = 100.60m,
                EntryMax = 101.00m,
                InvalidationPrice = 99.95m,
                Reason = "Synthetic BUY-like analysis for OpenAI critic smoke test only.",
                Model = "synthetic-smoke-test",
                IsSafeFallback = false
            };
        }

        private async Task<AiApiUsageRecord?> MarkSmokeRecordsAsync(
            string originalAgentType,
            string smokeAgentType,
            string model,
            string promptVersion,
            DateTime startedUtc,
            CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var analysisRecord = await db.AiAnalysisRecords
                .Where(x => x.TimestampUtc >= startedUtc.AddSeconds(-2)
                    && x.AgentType == originalAgentType
                    && x.Model == model
                    && x.PromptVersion == promptVersion
                    && x.Context.Contains(SmokeSymbol))
                .OrderByDescending(x => x.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (analysisRecord != null)
            {
                analysisRecord.AgentType = smokeAgentType;
            }

            var usageRecord = await db.AiApiUsageRecords
                .Where(x => x.TimestampUtc >= startedUtc.AddSeconds(-2)
                    && x.AgentType == originalAgentType
                    && x.Model == model
                    && x.PromptVersion == promptVersion
                    && x.Symbol == SmokeSymbol)
                .OrderByDescending(x => x.TimestampUtc)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (usageRecord != null)
            {
                usageRecord.RequestType = SmokeRequestType;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return usageRecord;
        }

        private bool IsAnalyzerSmokeResultValid(MarketSnapshot snapshot, AiMarketAnalysisResult result, out string reason)
        {
            if (result.IsSafeFallback)
            {
                reason = result.Reason;
                return false;
            }

            if (!_validator.TryValidate(snapshot, result, out reason)) return false;
            if (result.Action is not (AiMarketActions.Buy or AiMarketActions.Wait or AiMarketActions.Reject))
            {
                reason = "Analyzer returned an invalid action.";
                return false;
            }

            if (!string.Equals(result.Model, _settings.Analyzer.Model, StringComparison.Ordinal))
            {
                reason = "Analyzer result model does not match configured Analyzer model.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private bool IsCriticSmokeResultValid(AiTradeCriticResult result, out string reason)
        {
            if (result.IsSafeFallback)
            {
                reason = result.Reason;
                return false;
            }

            if (result.Confidence is < 0m or > 1m)
            {
                reason = "Critic confidence is outside 0..1.";
                return false;
            }

            if (result.RiskFlags == null)
            {
                reason = "Critic risk flags were not parsed.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(result.Reason))
            {
                reason = "Critic reason is required.";
                return false;
            }

            if (!string.Equals(result.Model, _settings.Critic.Model, StringComparison.Ordinal))
            {
                reason = "Critic result model does not match configured Critic model.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        private OpenAiSmokeTestStageResult BuildAnalyzerStage(AiMarketAnalysisResult result, AiApiUsageRecord? usage)
        {
            return new OpenAiSmokeTestStageResult
            {
                Success = !result.IsSafeFallback && usage?.Success == true,
                Model = _settings.Analyzer.Model,
                PromptVersion = _settings.Analyzer.PromptVersion,
                Action = result.Action,
                Confidence = result.Confidence,
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
                TotalTokens = usage?.TotalTokens,
                CachedInputTokens = usage?.CachedInputTokens,
                DurationMs = usage?.DurationMs ?? 0,
                Error = result.IsSafeFallback ? result.Reason : string.Empty,
                HttpStatus = ExtractHttpStatus(usage?.FailureReason)
            };
        }

        private OpenAiSmokeTestStageResult BuildCriticStage(AiTradeCriticResult result, AiApiUsageRecord? usage)
        {
            return new OpenAiSmokeTestStageResult
            {
                Success = !result.IsSafeFallback && usage?.Success == true,
                Model = _settings.Critic.Model,
                PromptVersion = _settings.Critic.PromptVersion,
                Approved = result.Approved,
                Confidence = result.Confidence,
                InputTokens = usage?.InputTokens,
                OutputTokens = usage?.OutputTokens,
                TotalTokens = usage?.TotalTokens,
                CachedInputTokens = usage?.CachedInputTokens,
                DurationMs = usage?.DurationMs ?? 0,
                Error = result.IsSafeFallback ? result.Reason : string.Empty,
                HttpStatus = ExtractHttpStatus(usage?.FailureReason)
            };
        }

        private static OpenAiSmokeTestResult Failure(
            string stage,
            string error,
            OpenAiSmokeTestStageResult analyzer,
            OpenAiSmokeTestStageResult critic,
            int? httpStatus = null)
        {
            return new OpenAiSmokeTestResult
            {
                Success = false,
                Stage = stage,
                Error = error,
                HttpStatus = httpStatus,
                Analyzer = analyzer,
                Critic = critic,
                TotalTokens = analyzer.TotalTokensOrZero() + critic.TotalTokensOrZero()
            };
        }

        private OpenAiSmokeTestStageResult NewAnalyzerStage()
        {
            return new OpenAiSmokeTestStageResult
            {
                Model = _settings.Analyzer.Model,
                PromptVersion = _settings.Analyzer.PromptVersion
            };
        }

        private OpenAiSmokeTestStageResult NewCriticStage()
        {
            return new OpenAiSmokeTestStageResult
            {
                Model = _settings.Critic.Model,
                PromptVersion = _settings.Critic.PromptVersion
            };
        }

        private static int? ExtractHttpStatus(string? failureReason)
        {
            if (string.IsNullOrWhiteSpace(failureReason)
                || !failureReason.StartsWith("HTTP ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var end = failureReason.IndexOf(':', StringComparison.Ordinal);
            var value = end > 5 ? failureReason[5..end] : failureReason[5..];
            return int.TryParse(value, out var status) ? status : null;
        }
    }

    internal static class OpenAiSmokeTestStageResultExtensions
    {
        public static int TotalTokensOrZero(this OpenAiSmokeTestStageResult stage)
        {
            return stage.TotalTokens ?? (stage.InputTokens ?? 0) + (stage.OutputTokens ?? 0);
        }

        public static OpenAiSmokeTestStageResult WithFailure(this OpenAiSmokeTestStageResult stage, string error, int? httpStatus)
        {
            return new OpenAiSmokeTestStageResult
            {
                Success = false,
                Model = stage.Model,
                PromptVersion = stage.PromptVersion,
                Action = stage.Action,
                Approved = stage.Approved,
                Confidence = stage.Confidence,
                InputTokens = stage.InputTokens,
                OutputTokens = stage.OutputTokens,
                TotalTokens = stage.TotalTokens,
                CachedInputTokens = stage.CachedInputTokens,
                DurationMs = stage.DurationMs,
                Error = error,
                HttpStatus = httpStatus
            };
        }
    }
}
