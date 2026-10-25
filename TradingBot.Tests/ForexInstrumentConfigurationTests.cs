using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure;

namespace TradingBot.Tests;

public sealed class ForexInstrumentConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" eurusd ")]
    [InlineData(" eur/usd ")]
    [InlineData("eur.usd")]
    public void PairAliasesNormalizeToOneIdentity(string? symbol)
    {
        var fx = Forex("USD");
        fx.Symbol = symbol!;
        fx.BaseCurrency = " eur ";
        fx.Currency = " usd ";
        fx.SecurityType = " cash ";
        fx.Exchange = " idealpro ";
        var settings = Mixed(fx);

        Assert.Empty(settings.GetValidationErrors());
        var pair = settings.GetConfiguredInstruments()[1];
        Assert.Equal(InstrumentType.Forex, pair.InstrumentType);
        Assert.Equal("EURUSD", pair.Symbol);
        Assert.Equal("EUR", pair.BaseCurrency);
        Assert.Equal("USD", pair.QuoteCurrency);
        Assert.Equal("EUR", pair.BrokerSymbol);
        Assert.Equal("EUR/USD", pair.DisplaySymbol);
        Assert.Equal("CASH|EURUSD|IDEALPRO|USD", pair.ContractKey);
        Assert.Equal("FX-CASH-EUR-USD-IDEALPRO", pair.InstrumentId);
    }

    [Fact]
    public void SameBaseCurrencyHasSeparateSymbolsStrategiesAndSignalIdentities()
    {
        var usd = Forex("USD");
        var gbp = Forex("GBP");
        usd.StrategyIds = ["eurusd-patterns", "eurusd-momentum"];
        usd.MaximumHoldingSeconds = 60;
        gbp.StrategyIds = ["eurgbp-patterns"];
        gbp.MaximumHoldingSeconds = 45;
        gbp.MarketDataTimeframes = ["5m"];
        var settings = Mixed(usd, gbp);

        Assert.Empty(settings.GetValidationErrors());
        var instruments = settings.GetConfiguredInstruments();
        Assert.Equal(new[] { "SPY", "EURUSD", "EURGBP" }, instruments.Select(x => x.Symbol));
        Assert.Equal(3, instruments.Select(x => x.ContractKey).Distinct().Count());
        Assert.Equal(usd.StrategyIds, instruments[1].StrategyIds);
        Assert.Equal(gbp.StrategyIds, instruments[2].StrategyIds);
        Assert.Equal(60, instruments[1].MaximumHoldingSeconds);
        Assert.Equal(45, instruments[2].MaximumHoldingSeconds);
        Assert.Equal(new[] { "5m" }, instruments[2].MarketDataTimeframes);
        Assert.NotEqual(PipelineContext.CreateForSignal(instruments[1].InstrumentId, "same-bar").SignalId,
            PipelineContext.CreateForSignal(instruments[2].InstrumentId, "same-bar").SignalId);
        Assert.Equal("SPY", Assert.Single(settings.GetEnabledInstruments()).Symbol);
    }

    [Theory]
    [InlineData("", "USD", "", "IDEALPRO", "BaseCurrency")]
    [InlineData("EU", "USD", "", "IDEALPRO", "BaseCurrency")]
    [InlineData("EUR", "U$D", "", "IDEALPRO", "quote Currency")]
    [InlineData("EUR", "EUR", "", "IDEALPRO", "different base")]
    [InlineData("EUR", "GBP", "EURUSD", "IDEALPRO", "Symbol must match")]
    [InlineData("EUR", "USD", "EUR", "IDEALPRO", "Symbol must match")]
    [InlineData("EUR", "USD", "E/U/RUSD", "IDEALPRO", "Symbol must match")]
    [InlineData("EUR", "USD", "", "SMART", "Exchange=IDEALPRO")]
    public void InvalidPairCannotReachNormalizedConfiguration(string basis, string quote, string symbol, string exchange, string reason)
    {
        var fx = Forex(quote);
        fx.BaseCurrency = basis;
        fx.Symbol = symbol;
        fx.Exchange = exchange;
        var settings = Mixed(fx);
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains(reason));
        Assert.Contains(reason, Assert.Throws<ArgumentException>(() => settings.GetConfiguredInstruments()).Message);
    }

    [Fact]
    public void DuplicatePairAndStockSymbolCollisionsAreRejected()
    {
        var alias = Forex("USD");
        alias.InstrumentId = "another-id";
        alias.Symbol = "eur/usd";
        var settings = Mixed(Forex("USD"), alias);
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains("Duplicate contract"));
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains("Duplicate Symbol"));
        settings.Instruments[0].Symbol = "EURUSD";
        settings.Instruments = settings.Instruments.Take(2).ToArray();
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains("Duplicate Symbol"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void ForexActivationFailsWithSpecificOptionsError(bool enabled, bool trading)
    {
        var fx = Forex("USD");
        fx.Enabled = enabled;
        fx.TradingEnabled = trading;
        var services = new ServiceCollection();
        services.AddOptions<TradingSettings>().Configure(x => x.Instruments = Mixed(fx).Instruments);
        services.AddInfrastructure();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TradingSettings>>().Value);
        Assert.Contains("FX-CASH-EUR-USD-IDEALPRO", error.Message);
        Assert.Contains("TradingEnabled=false", error.Message);
        Assert.Throws<ArgumentException>(() => Mixed(fx).GetEnabledInstruments());
    }

    [Fact]
    public void LegacyAndExplicitStockDefaultsRetainTheirContracts()
    {
        var legacy = new TradingSettings().GetConfiguredInstruments().Single();
        var stock = Mixed().GetConfiguredInstruments().Single();
        foreach (var item in new[] { legacy, stock })
        {
            Assert.Equal(InstrumentType.Stock, item.InstrumentType);
            Assert.Equal("SPY", item.Symbol);
            Assert.Equal("SPY", item.BrokerSymbol);
            Assert.Null(item.BaseCurrency);
            Assert.Equal("USD", item.QuoteCurrency);
            Assert.Equal("STK|SPY|SMART|USD", item.ContractKey);
        }
        Assert.Equal("SPY", legacy.InstrumentId);
        Assert.True(legacy.TradingEnabled);
        Assert.False(stock.TradingEnabled);
        Assert.Equal(new[] { PipelineContractVersions.DefaultStrategyId }, stock.StrategyIds);
        Assert.Equal(new[] { TradeDirection.Long }, stock.AllowedDirections);
    }

    [Fact]
    public void StockCannotSilentlyAcceptForexMetadata()
    {
        var settings = Mixed();
        settings.Instruments[0].BaseCurrency = "EUR";
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains("must not specify forex BaseCurrency"));
    }

    [Theory]
    [InlineData("FX")]
    [InlineData("unknown")]
    public void UnsupportedSecurityTypeIsExplicit(string securityType)
    {
        var settings = Mixed();
        settings.Instruments[0].SecurityType = securityType;
        Assert.Contains(settings.GetValidationErrors(), x => x.Contains("unsupported SecurityType"));
    }

    [Fact]
    public void CheckedInExampleBindsToSafeMixedConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(AppContext.BaseDirectory, "forex-instruments.example.json")).Build();
        var settings = configuration.GetSection("TradingSettings").Get<TradingSettings>()!;
        Assert.Empty(settings.GetValidationErrors());
        Assert.False(settings.Enabled);
        Assert.Equal(TradingOperatingMode.AnalysisOnly, settings.OperatingMode);
        Assert.False(settings.LiveTradingExplicitlyEnabled);
        Assert.Equal(new[] { "SPY", "EURUSD", "EURGBP" }, settings.GetConfiguredInstruments().Select(x => x.Symbol));
        Assert.All(settings.GetConfiguredInstruments(), x => Assert.False(x.TradingEnabled));
        Assert.Equal("SPY", Assert.Single(settings.GetEnabledInstruments()).Symbol);
        Assert.Equal(new[] { "eurusd-patterns" }, settings.GetConfiguredInstruments()[1].StrategyIds);
        Assert.Equal(new[] { "eurgbp-patterns" }, settings.GetConfiguredInstruments()[2].StrategyIds);
    }

    [Fact]
    public void BoundExplicitProfilesDoNotAppendStockDefaults()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
            { "Instruments": [{ "InstrumentId": "stock", "Symbol": "SPY",
                "StrategyIds": ["custom"], "AllowedDirections": ["Short"] }] }
            """));
        var settings = new ConfigurationBuilder().AddJsonStream(input).Build().Get<TradingSettings>()!;
        Assert.Empty(settings.GetValidationErrors());
        var instrument = Assert.Single(settings.GetConfiguredInstruments());
        Assert.Equal(new[] { "custom" }, instrument.StrategyIds);
        Assert.Equal(new[] { TradeDirection.Short }, instrument.AllowedDirections);
    }

    [Fact]
    public void MissingForexStrategyAndExplicitEmptyStockProfilesAreRejected()
    {
        var fx = Forex("USD");
        fx.StrategyIds = null;
        Assert.Contains(Mixed(fx).GetValidationErrors(), x => x.Contains("requires explicit StrategyIds"));
        var stock = Mixed();
        stock.Instruments[0].StrategyIds = [];
        stock.Instruments[0].AllowedDirections = [];
        Assert.Contains(stock.GetValidationErrors(), x => x.Contains("at least one StrategyId"));
        Assert.Contains(stock.GetValidationErrors(), x => x.Contains("at least one allowed direction"));
    }

    internal static InstrumentSettings Forex(string quote) => new()
    {
        InstrumentId = $"FX-CASH-EUR-{quote}-IDEALPRO", BaseCurrency = "EUR", Currency = quote,
        SecurityType = "CASH", Exchange = "IDEALPRO", Enabled = false, TradingEnabled = false,
        StrategyIds = ["fx-patterns"], MarketDataTimeframes = ["1m"]
    };

    private static TradingSettings Mixed(params InstrumentSettings[] pairs) => new()
    {
        Instruments = [new InstrumentSettings { InstrumentId = "US-STK-SPY-SMART", Symbol = "SPY" }, .. pairs]
    };
}
