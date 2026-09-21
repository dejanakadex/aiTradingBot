using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class AppSettingsSafetyTests
    {
        [Fact]
        public void AppSettings_DefaultsAreFailClosed()
        {
            var json = File.ReadAllText(FindRepoFile("TradingBot.Web", "appsettings.json"));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var trading = root.GetProperty("TradingSettings");
            Assert.Equal("AnalysisOnly", trading.GetProperty("OperatingMode").GetString());
            Assert.False(trading.GetProperty("LiveTradingExplicitlyEnabled").GetBoolean());
            Assert.Equal(0.6m, trading.GetProperty("MinimumPatternQualityForAiAnalysis").GetDecimal());

            var ibkr = root.GetProperty("IbkrSettings");
            Assert.False(string.Equals("LiveTrading", trading.GetProperty("OperatingMode").GetString(), StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(ibkr.GetProperty("HostType").GetString()));
            Assert.Equal(1, ibkr.GetProperty("ClientId").GetInt32());
            Assert.NotEqual(ibkr.GetProperty("Paper").GetProperty("Port").GetInt32(), ibkr.GetProperty("Live").GetProperty("Port").GetInt32());

            var risk = root.GetProperty("RiskSettings");
            Assert.Equal(1000m, risk.GetProperty("MaximumPositionValue").GetDecimal());
            Assert.Equal(10m, risk.GetProperty("MaximumRiskPerTrade").GetDecimal());
            Assert.Equal(50m, risk.GetProperty("MaximumDailyLoss").GetDecimal());
            Assert.Equal(1m, risk.GetProperty("MaximumLeverage").GetDecimal());
            Assert.Equal(1, risk.GetProperty("MaximumOpenPositions").GetInt32());
            Assert.Equal(3, risk.GetProperty("MaximumConsecutiveLosses").GetInt32());

            Assert.Equal("Data Source=data/trading.db", root.GetProperty("DatabaseSettings").GetProperty("ConnectionString").GetString());

            var openAi = root.GetProperty("OpenAiSettings");
            var configuredApiKey = openAi.GetProperty("ApiKey").GetString() ?? string.Empty;
            Assert.False(configuredApiKey.StartsWith("sk-", StringComparison.Ordinal), "OpenAiSettings:ApiKey must not contain a real OpenAI API key in appsettings.json.");
            Assert.Equal("OPENAI_API_KEY", openAi.GetProperty("ApiKeyName").GetString());
            Assert.False(openAi.TryGetProperty("Model", out _));
            Assert.False(openAi.TryGetProperty("AnalyzerPrompt" + "Version", out _));
            Assert.False(openAi.TryGetProperty("CriticPrompt" + "Version", out _));
            Assert.Equal("gpt-5.6-luna", openAi.GetProperty("Analyzer").GetProperty("Model").GetString());
            Assert.Equal("analyzer-v1", openAi.GetProperty("Analyzer").GetProperty("PromptVersion").GetString());
            Assert.Equal("gpt-5.6-luna", openAi.GetProperty("Critic").GetProperty("Model").GetString());
            Assert.Equal("critic-v1", openAi.GetProperty("Critic").GetProperty("PromptVersion").GetString());

            var pricing = openAi.GetProperty("Pricing");
            Assert.True(pricing.GetProperty("Analyzer").GetProperty("InputPricePerMillionTokens").GetDecimal() >= 0m);
            Assert.True(pricing.GetProperty("Analyzer").GetProperty("OutputPricePerMillionTokens").GetDecimal() >= 0m);
            Assert.True(pricing.GetProperty("Critic").GetProperty("InputPricePerMillionTokens").GetDecimal() >= 0m);
            Assert.True(pricing.GetProperty("Critic").GetProperty("OutputPricePerMillionTokens").GetDecimal() >= 0m);

            var openAiContext = openAi.GetProperty("MarketContext");
            Assert.Equal(60, openAiContext.GetProperty("OneMinuteCandles").GetInt32());
            Assert.Equal(50, openAiContext.GetProperty("FiveMinuteCandles").GetInt32());
            Assert.Equal(40, openAiContext.GetProperty("FifteenMinuteCandles").GetInt32());
        }

        private static string FindRepoFile(params string[] pathParts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "TradingBot.sln")))
                {
                    var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException($"Could not find {Path.Combine(pathParts)} from {AppContext.BaseDirectory}.");
        }
    }
}
