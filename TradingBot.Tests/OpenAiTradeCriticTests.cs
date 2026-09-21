using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public class OpenAiTradeCriticTests
    {
        [Fact]
        public async Task CritiqueAsync_SendsStructuredCriticRequestAndPersistsResult()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_CRITIC_SUCCESS";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new CapturingHandler(_ => SuccessfulResponse());
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var critic = CreateCritic(httpClient, factory, apiKeyName);

            var result = await critic.CritiqueAsync(BuildSnapshot(), BuildPattern(), BuildAnalysis());

            Assert.False(result.Approved);
            Assert.Equal(0.82m, result.Confidence);
            Assert.Contains("LARGE_SPREAD", result.RiskFlags);
            Assert.False(result.IsSafeFallback);
            Assert.Single(handler.RequestBodies);

            using var requestDocument = JsonDocument.Parse(handler.RequestBodies[0]);
            var root = requestDocument.RootElement;
            Assert.Equal("critic-model", root.GetProperty("model").GetString());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("ai_trade_critic", root.GetProperty("text").GetProperty("format").GetProperty("name").GetString());
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());

            var userContent = root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
            Assert.NotNull(userContent);
            using var contentDocument = JsonDocument.Parse(userContent!);
            Assert.True(contentDocument.RootElement.TryGetProperty("marketSnapshot", out _));
            Assert.True(contentDocument.RootElement.TryGetProperty("detectedPattern", out _));
            Assert.True(contentDocument.RootElement.TryGetProperty("aiMarketAnalysis", out _));
            Assert.Equal("critic-test-v1", contentDocument.RootElement.GetProperty("criticPromptVersion").GetString());

            await using (var db = factory.CreateDbContext())
            {
                var record = Assert.Single(await db.AiAnalysisRecords.ToListAsync());
                Assert.Equal("Critic", record.AgentType);
                Assert.Equal("critic-model", record.Model);
                Assert.Equal("critic-test-v1", record.PromptVersion);
                Assert.Contains("aiMarketAnalysis", record.Context);
                Assert.Contains("\"approved\":false", record.ResultJson);

                var usage = Assert.Single(await db.AiApiUsageRecords.ToListAsync());
                Assert.Equal("Critic", usage.AgentType);
                Assert.Equal("TradeCritic", usage.RequestType);
                Assert.Equal("SPY", usage.Symbol);
                Assert.Equal("BreakoutAndRetest", usage.Pattern);
                Assert.Equal("critic-model", usage.Model);
                Assert.Equal("critic-test-v1", usage.PromptVersion);
                Assert.Equal(120, usage.InputTokens);
                Assert.Equal(40, usage.OutputTokens);
                Assert.Equal(160, usage.TotalTokens);
                Assert.Equal(30, usage.CachedInputTokens);
                Assert.True(usage.Success);
            }

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task CritiqueAsync_MalformedResponseRejectsAndPersistsFallback()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_CRITIC_MALFORMED";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new CapturingHandler(_ => SuccessfulResponse("""{"approved":true,"confidence":1.5,"riskFlags":[],"reason":""}"""));
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var critic = CreateCritic(httpClient, factory, apiKeyName);

            var result = await critic.CritiqueAsync(BuildSnapshot(), BuildPattern(), BuildAnalysis());

            Assert.False(result.Approved);
            Assert.True(result.IsSafeFallback);
            Assert.Contains("REJECT", result.Reason);

            await using (var db = factory.CreateDbContext())
            {
                var record = Assert.Single(await db.AiAnalysisRecords.ToListAsync());
                Assert.Contains("\"isSafeFallback\":true", record.ResultJson);
                var usage = Assert.Single(await db.AiApiUsageRecords.ToListAsync());
                Assert.False(usage.Success);
                Assert.Contains("Invalid structured", usage.FailureReason);
            }

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task CritiqueAsync_AiErrorRejectsAndPersistsFallback()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_CRITIC_ERROR";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var critic = CreateCritic(httpClient, factory, apiKeyName);

            var result = await critic.CritiqueAsync(BuildSnapshot(), BuildPattern(), BuildAnalysis());

            Assert.False(result.Approved);
            Assert.True(result.IsSafeFallback);
            Assert.Contains("REJECT", result.Reason);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Single(await db.AiAnalysisRecords.ToListAsync());
            }

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task CritiqueAsync_MissingApiKeyRejectsWithoutHttpCall()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_CRITIC_MISSING";
            Environment.SetEnvironmentVariable(apiKeyName, null);
            var handler = new CapturingHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var critic = CreateCritic(httpClient, factory, apiKeyName);

            var result = await critic.CritiqueAsync(BuildSnapshot(), BuildPattern(), BuildAnalysis());

            Assert.False(result.Approved);
            Assert.True(result.IsSafeFallback);
            Assert.Empty(handler.RequestBodies);

            connection.Dispose();
        }

        private static OpenAiTradeCritic CreateCritic(
            HttpClient httpClient,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory,
            string apiKeyName)
        {
            var settings = Options.Create(new OpenAiSettings
            {
                ApiKeyName = apiKeyName,
                Analyzer = new AiAgentSettings
                {
                    Model = "analyzer-model",
                    PromptVersion = "analyzer-test-v1"
                },
                Critic = new AiAgentSettings
                {
                    Model = "critic-model",
                    PromptVersion = "critic-test-v1"
                },
                TimeoutSeconds = 5,
                MaxRetries = 0
            });

            return new OpenAiTradeCritic(httpClient, settings, factory, NullLogger<OpenAiTradeCritic>.Instance);
        }

        private static HttpResponseMessage SuccessfulResponse(string structuredContent = """{"approved":false,"confidence":0.82,"riskFlags":["LARGE_SPREAD","FALSE_BREAKOUT_RISK"],"reason":"Spread and breakout retest quality are not strong enough."}""")
        {
            var body = $$"""
            {
              "object": "response",
              "status": "completed",
              "output_text": {{JsonSerializer.Serialize(structuredContent)}},
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "output_text",
                      "text": {{JsonSerializer.Serialize(structuredContent)}}
                    }
                  ]
                }
              ],
              "usage": {
                "input_tokens": 120,
                "output_tokens": 40,
                "total_tokens": 160,
                "input_tokens_details": {
                  "cached_tokens": 30
                }
              }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static AiMarketAnalysisResult BuildAnalysis()
        {
            return new AiMarketAnalysisResult
            {
                Action = AiMarketActions.Buy,
                Confidence = 0.72m,
                PatternQuality = 0.68m,
                MarketRegime = "pullback in uptrend",
                ExpectedMovePercent = 0.8m,
                ExpectedHorizonMinutes = 20,
                EntryMin = 100.5m,
                EntryMax = 101.2m,
                InvalidationPrice = 99.4m,
                Reason = "Pattern is constructive but requires risk review."
            };
        }

        private static MarketSnapshot BuildSnapshot()
        {
            return new MarketSnapshot
            {
                Symbol = "SPY",
                CreatedAtUtc = DateTime.UtcNow,
                CurrentPrice = 101m,
                Spread = 0.08m,
                OneMinute = BuildContext(Timeframe.OneMinute, "Entry timing", 100m),
                FiveMinutes = BuildContext(Timeframe.FiveMinutes, "Trading setup / pullback context", 200m),
                FifteenMinutes = BuildContext(Timeframe.FifteenMinutes, "Broader market direction", 300m)
            };
        }

        private static MarketTimeframeSnapshot BuildContext(Timeframe timeframe, string interpretation, decimal basePrice)
        {
            var candles = Enumerable.Range(0, 3)
                .Select(i => new Candle("SPY", timeframe, DateTime.UtcNow.AddMinutes(-i), basePrice + i, basePrice + i + 1, basePrice + i - 1, basePrice + i + 0.5m, 1000 + i))
                .OrderBy(c => c.TimestampUtc)
                .ToList();

            return new MarketTimeframeSnapshot
            {
                Timeframe = timeframe,
                Interpretation = interpretation,
                RecentCandles = candles,
                Features = new MarketFeatures
                {
                    Symbol = "SPY",
                    TimestampUtc = candles[^1].TimestampUtc,
                    EmaShort = basePrice + 1,
                    EmaLong = basePrice,
                    Vwap = basePrice + 0.4m,
                    VolumeAverage = 1000m,
                    VolumeRatio = 1.2m,
                    Atr = 1.5m,
                    Volatility = 0.01m,
                    RecentHigh = candles.Max(c => c.High),
                    RecentLow = candles.Min(c => c.Low),
                    TrendDirection = 1
                },
                SupportResistanceCandidates = new[]
                {
                    new SupportResistanceCandidate { Kind = "Support", Price = candles.Min(c => c.Low), Source = "test" },
                    new SupportResistanceCandidate { Kind = "Resistance", Price = candles.Max(c => c.High), Source = "test" }
                },
                Trend = new TrendInformation { Direction = 1, Label = "Up", EmaShort = basePrice + 1, EmaLong = basePrice },
                Volume = new VolumeContext { LatestVolume = 1002m, AverageVolume = 1000m, VolumeRatio = 1.2m },
                Volatility = new VolatilityContext { Atr = 1.5m, Volatility = 0.01m, RecentHigh = candles.Max(c => c.High), RecentLow = candles.Min(c => c.Low) }
            };
        }

        private static PatternCandidate BuildPattern()
        {
            return new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", Timeframe.OneMinute, DateTime.UtcNow, 0.8m, new[] { 100m, 101m });
        }

        private static IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection connection)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBot.Persistence.TradingBotDbContext>()
                .UseSqlite(connection)
                .Options;

            var factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return factory;
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }

            public List<string> RequestBodies { get; } = new();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return _responseFactory(request);
            }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBot.Persistence.TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBot.Persistence.TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBot.Persistence.TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBot.Persistence.TradingBotDbContext CreateDbContext()
            {
                return new TradingBot.Persistence.TradingBotDbContext(_options);
            }

            public ValueTask<TradingBot.Persistence.TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBot.Persistence.TradingBotDbContext>(CreateDbContext());
            }
        }
    }
}
