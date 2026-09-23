using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Tests
{
    public sealed class TradingSettingsTests
    {
        [Fact]
        public void GetEnabledInstruments_NormalizesMultipleInstrumentDefinitions()
        {
            var settings = new TradingSettings
            {
                MarketDataTimeframes = new[] { "1 minute", "5m" },
                Instruments = new[]
                {
                    new InstrumentSettings
                    {
                        InstrumentId = "us-stk-spy-smart",
                        Symbol = " spy ",
                        Exchange = "smart",
                        Currency = "usd",
                        SecurityType = "stk",
                        Enabled = true,
                        TradingEnabled = false,
                        AllowedDirections = new[] { TradeDirection.Long, TradeDirection.Short },
                        StrategyIds = new[] { "deterministic-patterns", "momentum-scalper" }
                    },
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-QQQ-SMART",
                        Symbol = "qqq",
                        Enabled = true,
                        StrategyIds = new[] { "deterministic-patterns" },
                        MarketDataTimeframes = new[] { "5 minutes", "15m" }
                    },
                    new InstrumentSettings
                    {
                        InstrumentId = "US-STK-AAPL-SMART",
                        Symbol = "AAPL",
                        Enabled = false
                    }
                }
            };

            var instruments = settings.GetEnabledInstruments();

            Assert.Equal(2, instruments.Count);
            Assert.Equal("SPY", instruments[0].Symbol);
            Assert.Equal("SMART", instruments[0].Exchange);
            Assert.Equal(new[] { "1m", "5m" }, instruments[0].MarketDataTimeframes);
            Assert.Equal(new[] { TradeDirection.Long, TradeDirection.Short }, instruments[0].AllowedDirections);
            Assert.Equal(new[] { "5m", "15m" }, instruments[1].MarketDataTimeframes);
        }

        [Fact]
        public void GetEnabledInstruments_SupportsLegacySymbolsDuringMigration()
        {
            var settings = new TradingSettings
            {
                Symbols = new[] { " spy ", "QQQ", "spy" },
                Instruments = Array.Empty<InstrumentSettings>(),
                MarketDataTimeframes = new[] { "1m" }
            };

            var instruments = settings.GetEnabledInstruments();

            Assert.Equal(new[] { "SPY", "QQQ" }, instruments.Select(instrument => instrument.Symbol));
            Assert.All(instruments, instrument => Assert.True(instrument.TradingEnabled));
        }

        [Fact]
        public void GetValidationErrors_RejectsAmbiguousOrUnsafeDefinitions()
        {
            var settings = new TradingSettings
            {
                Instruments = new[]
                {
                    new InstrumentSettings
                    {
                        InstrumentId = "duplicate",
                        Symbol = "SPY",
                        Enabled = false,
                        TradingEnabled = true,
                        StrategyIds = new[] { "same", "SAME" },
                        MarketDataTimeframes = new[] { "unknown" }
                    },
                    new InstrumentSettings
                    {
                        InstrumentId = "DUPLICATE",
                        Symbol = "spy",
                        Enabled = false
                    }
                }
            };

            var errors = settings.GetValidationErrors();

            Assert.Contains(errors, error => error.Contains("Duplicate InstrumentId"));
            Assert.Contains(errors, error => error.Contains("Duplicate Symbol"));
            Assert.Contains(errors, error => error.Contains("cannot enable trading while disabled"));
            Assert.Contains(errors, error => error.Contains("duplicate StrategyIds"));
            Assert.Contains(errors, error => error.Contains("supported market-data timeframe"));
            Assert.Contains(errors, error => error.Contains("at least one enabled instrument"));
        }

        [Fact]
        public void PipelineContext_UsesDeterministicSignalIdAndUniqueCorrelationId()
        {
            var first = PipelineContext.CreateForSignal("US-STK-SPY-SMART", "Hammer|OneMinute|2026-09-23T10:00:00Z");
            var replay = PipelineContext.CreateForSignal("us-stk-spy-smart", "hammer|oneminute|2026-09-23t10:00:00z");
            var otherSignal = PipelineContext.CreateForSignal("US-STK-SPY-SMART", "Hammer|OneMinute|2026-09-23T10:01:00Z");

            Assert.Equal(first.SignalId, replay.SignalId);
            Assert.NotEqual(first.CorrelationId, replay.CorrelationId);
            Assert.NotEqual(first.SignalId, otherSignal.SignalId);
            Assert.Equal(PipelineContractVersions.MarketData, first.MarketDataVersion);
            Assert.Equal(PipelineContractVersions.Features, first.FeatureVersion);
            Assert.Equal(PipelineContractVersions.Patterns, first.PatternVersion);
            Assert.Equal(PipelineContractVersions.Strategy, first.StrategyVersion);
        }
    }
}
