using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Web;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class OpenAiSmokeTestServiceTests
    {
        [Fact]
        public async Task RunAsync_MissingApiKeyFailsBeforeHttpCall()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_MISSING_KEY";
            Environment.SetEnvironmentVariable(apiKeyName, null);
            var handler = new QueueingHandler();
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.Equal("Configuration", result.Stage);
            Assert.Contains(apiKeyName, result.Error);
            Assert.Equal(0, handler.CallCount);

            connection.Dispose();
        }

        [Fact]
        public async Task RunAsync_MissingAnalyzerModelFailsBeforeHttpCall()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_MISSING_ANALYZER_MODEL";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler();
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName, analyzerModel: string.Empty);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.Equal("Configuration", result.Stage);
            Assert.Contains("Analyzer:Model", result.Error);
            Assert.Equal(0, handler.CallCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task RunAsync_MissingCriticModelFailsBeforeHttpCall()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_MISSING_CRITIC_MODEL";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler();
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName, criticModel: string.Empty);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.Equal("Configuration", result.Stage);
            Assert.Contains("Critic:Model", result.Error);
            Assert.Equal(0, handler.CallCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task RunAsync_AnalyzerApiFailureReturnsSafeStatusAndMessage()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_ANALYZER_FAILURE";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler(ModelUnavailableResponse());
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.Equal("Analyzer", result.Stage);
            Assert.Equal(400, result.HttpStatus);
            Assert.Contains("Configured model is unavailable or inaccessible", result.Error);
            Assert.Equal(1, handler.CallCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task RunAsync_CriticApiFailureStopsAfterCritic()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_CRITIC_FAILURE";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler(AnalyzerSuccessResponse(), RateLimitResponse());
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.True(result.Analyzer.Success);
            Assert.Equal("Critic", result.Stage);
            Assert.Equal(429, result.HttpStatus);
            Assert.Contains("quota, billing, or rate limit", result.Error);
            Assert.Equal(2, handler.CallCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task RunAsync_InvalidStructuredOutputFailsClosed()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_INVALID_OUTPUT";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler(AnalyzerSuccessResponse("""{"action":"BUY","confidence":1.5,"patternQuality":0.82,"marketRegime":"up","expectedMovePercent":0.7,"expectedHorizonMinutes":20,"entryMin":100.6,"entryMax":100.9,"invalidationPrice":99.9,"reason":"bad"}"""));
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName);

            var result = await service.RunAsync();

            Assert.False(result.Success);
            Assert.Equal("Analyzer", result.Stage);
            Assert.Contains("invalid structured analysis", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, handler.CallCount);

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public async Task RunAsync_SuccessfulAnalyzerAndCriticMarksUsageAsSmokeTest()
        {
            const string apiKeyName = "TRADINGBOT_SMOKE_SUCCESS";
            Environment.SetEnvironmentVariable(apiKeyName, "test-key");
            var handler = new QueueingHandler(AnalyzerSuccessResponse(), CriticSuccessResponse());
            using var httpClient = new HttpClient(handler);
            var factory = CreateInMemoryFactory(out var connection);
            var service = CreateService(httpClient, factory, apiKeyName);

            var result = await service.RunAsync();

            Assert.True(result.Success);
            Assert.Equal("Completed", result.Stage);
            Assert.True(result.Analyzer.Success);
            Assert.Equal("BUY", result.Analyzer.Action);
            Assert.Equal("analyzer-model", result.Analyzer.Model);
            Assert.Equal("analyzer-smoke-v1", result.Analyzer.PromptVersion);
            Assert.Equal(111, result.Analyzer.InputTokens);
            Assert.Equal(22, result.Analyzer.OutputTokens);
            Assert.Equal(133, result.Analyzer.TotalTokens);
            Assert.True(result.Critic.Success);
            Assert.True(result.Critic.Approved);
            Assert.Equal("critic-model", result.Critic.Model);
            Assert.Equal("critic-smoke-v1", result.Critic.PromptVersion);
            Assert.Equal(144, result.Critic.InputTokens);
            Assert.Equal(33, result.Critic.OutputTokens);
            Assert.Equal(177, result.Critic.TotalTokens);
            Assert.Equal(310, result.TotalTokens);

            await using (var db = factory.CreateDbContext())
            {
                var usage = await db.AiApiUsageRecords.OrderBy(x => x.Id).ToListAsync();
                Assert.Equal(2, usage.Count);
                Assert.All(usage, x => Assert.Equal("SmokeTest", x.RequestType));

                var analysisRecords = await db.AiAnalysisRecords.OrderBy(x => x.Id).ToListAsync();
                Assert.Contains(analysisRecords, x => x.AgentType == "SmokeTestAnalyzer");
                Assert.Contains(analysisRecords, x => x.AgentType == "SmokeTestCritic");
            }

            connection.Dispose();
            Environment.SetEnvironmentVariable(apiKeyName, null);
        }

        [Fact]
        public void SmokeTestServiceCannotReachTradingExecutionServices()
        {
            var constructorParameterTypes = typeof(OpenAiSmokeTestService)
                .GetConstructors()
                .SelectMany(ctor => ctor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToList();

            Assert.DoesNotContain(typeof(IOrderExecutionService), constructorParameterTypes);
            Assert.DoesNotContain(typeof(IOrderManager), constructorParameterTypes);
            Assert.DoesNotContain(typeof(IStrategyEngine), constructorParameterTypes);
            Assert.DoesNotContain(typeof(IRiskEngine), constructorParameterTypes);
        }

        [Fact]
        public void DevelopmentEndpointGuard_HidesEndpointOutsideDevelopment()
        {
            Assert.False(DevelopmentEndpointGuard.ShouldExposeDevelopmentEndpoints(new TestHostEnvironment("Production")));
            Assert.False(DevelopmentEndpointGuard.ShouldExposeDevelopmentEndpoints(new TestHostEnvironment("Staging")));
            Assert.True(DevelopmentEndpointGuard.ShouldExposeDevelopmentEndpoints(new TestHostEnvironment("Development")));
        }

        private static OpenAiSmokeTestService CreateService(
            HttpClient httpClient,
            IDbContextFactory<TradingBotDbContext> factory,
            string apiKeyName,
            string analyzerModel = "analyzer-model",
            string criticModel = "critic-model")
        {
            var settings = Options.Create(new OpenAiSettings
            {
                ApiKeyName = apiKeyName,
                ResponsesEndpoint = "https://api.openai.com/v1/responses",
                Analyzer = new AiAgentSettings { Model = analyzerModel, PromptVersion = "analyzer-smoke-v1" },
                Critic = new AiAgentSettings { Model = criticModel, PromptVersion = "critic-smoke-v1" },
                TimeoutSeconds = 5,
                MaxRetries = 0,
                MaximumAiCallsPerMinute = 100,
                MaximumAiCallsPerDay = 1000,
                MaximumAiInputTokensPerDay = 100000,
                MaximumAiOutputTokensPerDay = 100000
            });

            var tradingOptions = Options.Create(new TradingSettings { MaximumAiEntryDeviationPercent = 10m });
            var validator = new AiAnalysisValidator(tradingOptions);
            var apiKeyProvider = new OpenAiApiKeyProvider(settings);
            var analyzer = new OpenAiMarketAnalyzer(
                httpClient,
                settings,
                factory,
                validator,
                new AllowingAiUsageLimiter(),
                apiKeyProvider,
                NullLogger<OpenAiMarketAnalyzer>.Instance);
            var critic = new OpenAiTradeCritic(
                httpClient,
                settings,
                factory,
                new AllowingAiUsageLimiter(),
                apiKeyProvider,
                NullLogger<OpenAiTradeCritic>.Instance);

            return new OpenAiSmokeTestService(
                analyzer,
                critic,
                validator,
                factory,
                apiKeyProvider,
                settings,
                NullLogger<OpenAiSmokeTestService>.Instance);
        }

        private static HttpResponseMessage AnalyzerSuccessResponse(string structuredContent = """{"action":"BUY","confidence":0.78,"patternQuality":0.82,"marketRegime":"pullback in uptrend above VWAP","expectedMovePercent":0.75,"expectedHorizonMinutes":20,"entryMin":100.6,"entryMax":100.9,"invalidationPrice":99.9,"reason":"Synthetic setup is coherent enough for smoke testing."}""")
        {
            return ResponsesApiResponse(structuredContent, 111, 22, 133, 9);
        }

        private static HttpResponseMessage CriticSuccessResponse(string structuredContent = """{"approved":true,"confidence":0.74,"riskFlags":[],"reason":"No material rejection flags in the synthetic smoke test."}""")
        {
            return ResponsesApiResponse(structuredContent, 144, 33, 177, 11);
        }

        private static HttpResponseMessage ResponsesApiResponse(string structuredContent, int inputTokens, int outputTokens, int totalTokens, int cachedTokens)
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
                "input_tokens": {{inputTokens}},
                "output_tokens": {{outputTokens}},
                "total_tokens": {{totalTokens}},
                "input_tokens_details": {
                  "cached_tokens": {{cachedTokens}}
                }
              }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage ModelUnavailableResponse()
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"The model `missing-model` does not exist or you do not have access to it."}}""", Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage RateLimitResponse()
        {
            return new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("""{"error":{"message":"You exceeded your current quota, please check your plan and billing details."}}""", Encoding.UTF8, "application/json")
            };
        }

        private static IDbContextFactory<TradingBotDbContext> CreateInMemoryFactory(out SqliteConnection connection)
        {
            connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<TradingBotDbContext>()
                .UseSqlite(connection)
                .Options;

            var factory = new SimpleDbContextFactory(options);
            using var db = factory.CreateDbContext();
            db.Database.EnsureCreated();
            return factory;
        }

        private sealed class QueueingHandler : HttpMessageHandler
        {
            private readonly Queue<HttpResponseMessage> _responses;

            public QueueingHandler(params HttpResponseMessage[] responses)
            {
                _responses = new Queue<HttpResponseMessage>(responses);
            }

            public int CallCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;
                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("Unexpected HTTP call.");
                }

                return Task.FromResult(_responses.Dequeue());
            }
        }

        private sealed class SimpleDbContextFactory : IDbContextFactory<TradingBotDbContext>
        {
            private readonly DbContextOptions<TradingBotDbContext> _options;

            public SimpleDbContextFactory(DbContextOptions<TradingBotDbContext> options)
            {
                _options = options;
            }

            public TradingBotDbContext CreateDbContext()
            {
                return new TradingBotDbContext(_options);
            }

            public ValueTask<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                return new ValueTask<TradingBotDbContext>(CreateDbContext());
            }
        }

        private sealed class AllowingAiUsageLimiter : IAiUsageLimiter
        {
            public Task<AiUsageLimitDecision> CheckAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(AiUsageLimitDecision.Allow());
            }
        }

        private sealed class TestHostEnvironment : IHostEnvironment
        {
            public TestHostEnvironment(string environmentName)
            {
                EnvironmentName = environmentName;
            }

            public string EnvironmentName { get; set; }
            public string ApplicationName { get; set; } = "TradingBot.Tests";
            public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
            public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        }
    }
}
