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
    public class OpenAiMarketAnalyzerTests
    {
        [Fact]
        public async Task AnalyzeAsync_SendsStructuredMarketDataAndPersistsResult()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_KEY_SUCCESS";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new CapturingHandler(_ => SuccessfulResponse());
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var analyzer = CreateAnalyzer(httpClient, factory, apiKeyName);

            var result = await analyzer.AnalyzeAsync(BuildSnapshot(), BuildPattern(Timeframe.OneMinute));

            Assert.Equal(AiMarketActions.Wait, result.Action);
            Assert.False(result.IsSafeFallback);
            Assert.Single(handler.RequestBodies);

            using var requestDocument = JsonDocument.Parse(handler.RequestBodies[0]);
            var root = requestDocument.RootElement;
            Assert.Equal("analyzer-model", root.GetProperty("model").GetString());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.Equal("json_schema", root.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal("ai_market_analysis", root.GetProperty("text").GetProperty("format").GetProperty("name").GetString());
            Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());

            var userContent = root.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString();
            Assert.NotNull(userContent);
            using var contentDocument = JsonDocument.Parse(userContent!);
            Assert.True(contentDocument.RootElement.TryGetProperty("marketSnapshot", out var marketSnapshot));
            Assert.True(marketSnapshot.TryGetProperty("oneMinute", out _));
            Assert.True(marketSnapshot.TryGetProperty("fiveMinutes", out _));
            Assert.True(marketSnapshot.TryGetProperty("fifteenMinutes", out _));
            Assert.True(marketSnapshot.TryGetProperty("spread", out _));
            Assert.Equal("analyzer-test-v1", contentDocument.RootElement.GetProperty("analyzerPromptVersion").GetString());

            await using (var db = factory.CreateDbContext())
            {
                var record = Assert.Single(await db.AiAnalysisRecords.ToListAsync());
                Assert.Equal("Analyzer", record.AgentType);
                Assert.Equal("analyzer-model", record.Model);
                Assert.Equal("analyzer-test-v1", record.PromptVersion);
                Assert.Contains("marketSnapshot", record.Context);
                Assert.Contains("\"action\":\"WAIT\"", record.ResultJson);

                var usage = Assert.Single(await db.AiApiUsageRecords.ToListAsync());
                Assert.Equal("Analyzer", usage.AgentType);
                Assert.Equal("MarketAnalyzer", usage.RequestType);
                Assert.Equal("SPY", usage.Symbol);
                Assert.Equal("BreakoutAndRetest", usage.Pattern);
                Assert.Equal("analyzer-model", usage.Model);
                Assert.Equal("analyzer-test-v1", usage.PromptVersion);
                Assert.Equal(100, usage.InputTokens);
                Assert.Equal(50, usage.OutputTokens);
                Assert.Equal(150, usage.TotalTokens);
                Assert.Equal(25, usage.CachedInputTokens);
                Assert.True(usage.Success);
            }

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task AnalyzeAsync_RetriesTransientFailure()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_KEY_RETRY";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var callCount = 0;
            var handler = new CapturingHandler(_ =>
            {
                callCount++;
                return callCount == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
                    : SuccessfulResponse();
            });

            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var analyzer = CreateAnalyzer(httpClient, factory, apiKeyName, maxRetries: 1);

            var result = await analyzer.AnalyzeAsync(BuildSnapshot());

            Assert.Equal(AiMarketActions.Wait, result.Action);
            Assert.Equal(2, callCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task AnalyzeAsync_InvalidResponseReturnsNoTradeFallbackAndPersists()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_KEY_INVALID";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new CapturingHandler(_ => SuccessfulResponse("""{"action":"BUY","confidence":1.5,"patternQuality":0.5,"marketRegime":"up","expectedMovePercent":1,"expectedHorizonMinutes":15,"entryMin":101,"entryMax":100,"invalidationPrice":99,"reason":"bad"}"""));
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var analyzer = CreateAnalyzer(httpClient, factory, apiKeyName);

            var result = await analyzer.AnalyzeAsync(BuildSnapshot());

            Assert.Equal(AiMarketActions.Reject, result.Action);
            Assert.True(result.IsSafeFallback);
            Assert.Contains("NO TRADE", result.Reason);

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
        public async Task AnalyzeAsync_MissingApiKeyReturnsNoTradeWithoutHttpCall()
        {
            const string apiKeyName = "TRADINGBOT_TEST_OPENAI_KEY_MISSING";
            Environment.SetEnvironmentVariable(apiKeyName, null);
            var handler = new CapturingHandler(_ => throw new InvalidOperationException("HTTP should not be called"));
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var analyzer = CreateAnalyzer(httpClient, factory, apiKeyName);

            var result = await analyzer.AnalyzeAsync(BuildSnapshot());

            Assert.Equal(AiMarketActions.Reject, result.Action);
            Assert.True(result.IsSafeFallback);
            Assert.Empty(handler.RequestBodies);

            await using (var db = factory.CreateDbContext())
            {
                Assert.Single(await db.AiAnalysisRecords.ToListAsync());
            }

            connection.Dispose();
        }

        private static OpenAiMarketAnalyzer CreateAnalyzer(
            HttpClient httpClient,
            IDbContextFactory<TradingBot.Persistence.TradingBotDbContext> factory,
            string apiKeyName,
            string analyzerModel = "analyzer-model",
            string analyzerPromptVersion = "analyzer-test-v1",
            int maxRetries = 0)
        {
            var settings = Options.Create(new OpenAiSettings
            {
                ApiKeyName = apiKeyName,
                Analyzer = new AiAgentSettings
                {
                    Model = analyzerModel,
                    PromptVersion = analyzerPromptVersion
                },
                Critic = new AiAgentSettings
                {
                    Model = "critic-model",
                    PromptVersion = "critic-test-v1"
                },
                TimeoutSeconds = 5,
                MaxRetries = maxRetries
            });

            return new OpenAiMarketAnalyzer(httpClient, settings, factory, NullLogger<OpenAiMarketAnalyzer>.Instance);
        }

        private static HttpResponseMessage SuccessfulResponse(string structuredContent = """{"action":"WAIT","confidence":0.65,"patternQuality":0.7,"marketRegime":"pullback in uptrend","expectedMovePercent":0.8,"expectedHorizonMinutes":20,"entryMin":100.5,"entryMax":101.2,"invalidationPrice":99.4,"reason":"Pattern is constructive but needs confirmation."}""")
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
                "input_tokens": 100,
                "output_tokens": 50,
                "total_tokens": 150,
                "input_tokens_details": {
                  "cached_tokens": 25
                }
              }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static MarketSnapshot BuildSnapshot()
        {
            return new MarketSnapshot
            {
                Symbol = "SPY",
                CreatedAtUtc = DateTime.UtcNow,
                CurrentPrice = 101m,
                Spread = 0.02m,
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
                DetectedPatterns = new[] { BuildPattern(timeframe) },
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

        private static PatternCandidate BuildPattern(Timeframe timeframe)
        {
            return new PatternCandidate(PatternType.BreakoutAndRetest, "SPY", timeframe, DateTime.UtcNow, 0.8m, new[] { 100m, 101m });
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
