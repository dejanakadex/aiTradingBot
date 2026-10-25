using System.Reflection;
using System.Text.Json;
using IBApi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.ShadowProbe;

namespace TradingBot.Tests;

public sealed class IbkrFxContractTests
{
    internal static TradingSettings Settings() => new()
    {
        Instruments =
        [
            new() { InstrumentId = "SPY-ID", Symbol = "SPY", TradingEnabled = false },
            new() { InstrumentId = "QQQ-ID", Symbol = "QQQ", TradingEnabled = false },
            Forex("USD"), Forex("GBP")
        ]
    };

    private static InstrumentSettings Forex(string quote) => new()
    {
        InstrumentId = $"EUR{quote}-ID", BaseCurrency = "EUR", Currency = quote,
        SecurityType = "CASH", Exchange = "IDEALPRO", Enabled = false, TradingEnabled = false,
        StrategyIds = [$"eur{quote.ToLowerInvariant()}-patterns"]
    };

    [Theory]
    [InlineData("SPY", "SPY", "STK", "SMART", "USD")]
    [InlineData("QQQ", "QQQ", "STK", "SMART", "USD")]
    [InlineData("EURUSD", "EUR", "CASH", "IDEALPRO", "USD")]
    [InlineData("EURGBP", "EUR", "CASH", "IDEALPRO", "GBP")]
    public void ConfiguredIdentityProducesCorrectBrokerContract(string symbol, string brokerSymbol,
        string type, string exchange, string quote)
    {
        var instrument = Instrument(symbol);
        var contract = IbkrContractFactory.Create(instrument);
        Assert.Equal(brokerSymbol, contract.Symbol);
        Assert.Equal(type, contract.SecType);
        Assert.Equal(exchange, contract.Exchange);
        Assert.Equal(quote, contract.Currency);
        Assert.Equal(symbol, instrument.Symbol);
        Assert.Equal(0, contract.ConId); // Identity must be resolved by IBKR, never guessed.
    }

    [Theory]
    [InlineData("SPY")]
    [InlineData("EURUSD")]
    [InlineData("EURGBP")]
    public void ConfirmedDetailsRetainBrokerMetadataWithoutInferringExecutionPermission(string symbol)
    {
        var instrument = Instrument(symbol);
        var detail = Details(instrument);
        var probe = IbkrContractFactory.Evaluate(instrument, [detail]);
        Assert.Equal("Confirmed", probe.Status);
        Assert.Equal(12345, probe.ContractId);
        Assert.Equal(1, probe.MatchCount);
        Assert.Equal(instrument.BrokerSymbol, probe.Metadata!.Contract.Symbol);
        Assert.Equal(0.00005m, probe.Metadata.MinimumTick);
        Assert.Equal(20000m, probe.Metadata.MinimumSize);
        Assert.Equal(1m, probe.Metadata.SizeIncrement);
        Assert.Equal("UTC", probe.Metadata.TimeZoneId);
        Assert.Equal("239", probe.Metadata.MarketRuleIds);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("quote")]
    [InlineData("type")]
    [InlineData("exchange")]
    [InlineData("id")]
    [InlineData("internal-pair-as-broker-symbol")]
    public void RejectsWrongOrIncompleteForexResponse(string mismatch)
    {
        var instrument = Instrument("EURUSD");
        var detail = Details(instrument);
        switch (mismatch)
        {
            case "base": detail.Contract.Symbol = "GBP"; break;
            case "quote": detail.Contract.Currency = "GBP"; break;
            case "type": detail.Contract.SecType = "STK"; break;
            case "exchange": detail.Contract.Exchange = "IDEAL"; break;
            case "id": detail.Contract.ConId = 0; break;
            default: detail.Contract.Symbol = "EURUSD"; break;
        }
        Assert.Equal("Mismatch", IbkrContractFactory.Evaluate(instrument, [detail]).Status);
    }

    [Fact]
    public void MissingAmbiguousAndUnknownMetadataAreNotInvented()
    {
        var instrument = Instrument("EURUSD");
        Assert.Equal("NotFound", IbkrContractFactory.Evaluate(instrument, []).Status);
        var detail = Details(instrument);
        Assert.Equal("Ambiguous", IbkrContractFactory.Evaluate(instrument, [detail, Details(Instrument("EURGBP"))]).Status);
        detail.MinTick = double.MaxValue;
        detail.MinSize = decimal.MaxValue;
        detail.SizeIncrement = 0;
        detail.SuggestedSizeIncrement = -1;
        var metadata = IbkrContractFactory.Evaluate(instrument, [detail]).Metadata!;
        Assert.Null(metadata.MinimumTick);
        Assert.Null(metadata.MinimumSize);
        Assert.Null(metadata.SizeIncrement);
        Assert.Null(metadata.SuggestedSizeIncrement);
    }

    [Fact]
    public void UnsupportedContractsCannotSilentlyBecomeUsStocks()
    {
        Assert.Throws<NotSupportedException>(() => IbkrContractFactory.Create(Instrument("SPY") with { Currency = "EUR" }));
        Assert.Throws<NotSupportedException>(() => IbkrContractFactory.Create(Instrument("SPY") with { SecurityType = "OPT" }));
        Assert.Throws<NotSupportedException>(() => IbkrContractFactory.Create(Instrument("EURUSD") with { Exchange = "SMART" }));
        Assert.Throws<NotSupportedException>(() => IbkrContractFactory.Create(Instrument("EURUSD") with { Symbol = "EUR" }));
    }

    [Fact]
    public async Task RealAdapterSeparatesConcurrentPairsSharingBaseCurrency()
    {
        using var broker = Broker();
        var usd = AttachRequest(broker, 101, Instrument("EURUSD"));
        var gbp = AttachRequest(broker, 102, Instrument("EURGBP"));
        broker.contractDetails(102, Details(Instrument("EURGBP")));
        broker.contractDetails(101, Details(Instrument("EURUSD")));
        Assert.False(usd.IsCompleted);
        broker.contractDetailsEnd(102);
        broker.contractDetailsEnd(101);
        Assert.Equal("USD", (await usd).Metadata!.Contract.Currency);
        Assert.Equal("GBP", (await gbp).Metadata!.Contract.Currency);
        Assert.Equal("Confirmed", (await usd).Status);
        Assert.Equal("Confirmed", (await gbp).Status);
    }

    [Fact]
    public async Task BrokerRejectionPreservesErrorCodeWithoutExportingRawMessage()
    {
        using var broker = Broker();
        var completion = AttachRequest(broker, 101, Instrument("EURUSD"));
        broker.error(101, 0, 200, "PRIVATE_ACCOUNT_MARKER", "PRIVATE_REJECT_MARKER");
        var result = await completion;
        Assert.Equal("BrokerError", result.Status);
        Assert.Equal(200, result.ErrorCode);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task ForexDataAndOrdersRemainBlockedBeforeAnyConnection()
    {
        using var broker = Broker();
        var order = new OrderRequestDto { Symbol = "EURUSD", Quantity = 20000, Side = "BUY", Type = "MARKET" };
        await Assert.ThrowsAsync<NotSupportedException>(() => broker.SubmitOrderAsync(order));
        await Assert.ThrowsAsync<NotSupportedException>(() => broker.ModifyOrderAsync("1", order));
        await Assert.ThrowsAsync<NotSupportedException>(() => broker.SubscribeAsync("EURUSD", "1m"));
        await Assert.ThrowsAsync<NotSupportedException>(() => broker.SubscribeTicksAsync("EURUSD-ID", "EURUSD"));
        await Assert.ThrowsAsync<NotSupportedException>(() => broker.GetHistoricalBarsAsync(new HistoricalBarRequest(
            "EURUSD-ID", "EURUSD", "1m", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow)));
    }

    [Fact]
    public async Task UnknownSymbolsAndMismatchedIdsFailBeforeAnyConnection()
    {
        using var broker = Broker();
        await Assert.ThrowsAsync<ArgumentException>(() => broker.SubmitOrderAsync(new OrderRequestDto { Symbol = "EUR" }));
        await Assert.ThrowsAsync<ArgumentException>(() => broker.SubscribeTicksAsync("EURGBP-ID", "EURUSD"));
        await Assert.ThrowsAsync<ArgumentException>(() => broker.GetHistoricalBarsAsync(new HistoricalBarRequest(
            "QQQ-ID", "SPY", "1m", DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow)));
        await Assert.ThrowsAsync<ArgumentException>(() => broker.ProbeContractAsync(Instrument("EURUSD") with { Exchange = "SMART" }));
        await Assert.ThrowsAsync<ArgumentException>(() => broker.ProbeContractAsync(Instrument("EURUSD") with { InstrumentId = "unknown" }));
        // A valid stock request reaches the ordinary disconnected check rather than an FX guard.
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubscribeTicksAsync("SPY-ID", "SPY"));
    }

    [Fact]
    public async Task ReadOnlyDiagnosticsRejectAllOrderMutationsIncludingStocks()
    {
        using var broker = Broker(readOnly: true);
        var order = new OrderRequestDto { Symbol = "SPY", Quantity = 1, Side = "BUY", Type = "MARKET" };
        var submit = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubmitOrderAsync(order));
        Assert.Contains("read-only", submit.Message);
        var modify = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ModifyOrderAsync("1", order));
        Assert.Contains("read-only", modify.Message);
        var cancel = await Assert.ThrowsAsync<InvalidOperationException>(() => broker.CancelOrderAsync("1"));
        Assert.Contains("read-only", cancel.Message);
    }

    [Fact]
    public void ReadOnlyDiagnosticsNeverPublishOrderReadiness()
    {
        using var broker = Broker(readOnly: true);
        bool? ready = null;
        broker.ReadinessChanged += value => { ready = value; return Task.CompletedTask; };
        broker.nextValidId(123);
        Assert.False(ready);
    }

    [Theory]
    [InlineData("{\"TradingSettings\":{\"Enabled\":true}}")]
    [InlineData("{\"TradingSettings\":{\"LiveTradingExplicitlyEnabled\":true}}")]
    [InlineData("{\"TradingSettings\":{\"OperatingMode\":\"PaperTrading\"}}")]
    [InlineData("[]")]
    public void ContractCliRejectsUnsafeOrInvalidConfiguration(string json) =>
        Assert.Throws<ArgumentException>(() => ContractProbeRunner.ReadConfiguration(json));

    [Fact]
    public void ContractCliAcceptsMixedDisabledForexConfigButRejectsTradingActivation()
    {
        var settings = Settings();
        var json = JsonSerializer.Serialize(new { TradingSettings = settings });
        Assert.Equal(4, ContractProbeRunner.ReadConfiguration(json).GetConfiguredInstruments().Count);
        settings.Instruments[0].TradingEnabled = true;
        Assert.Throws<ArgumentException>(() => ContractProbeRunner.ReadConfiguration(JsonSerializer.Serialize(new { TradingSettings = settings })));
        Assert.Throws<ArgumentException>(() => ContractProbeRunner.ReadConfiguration("{}"));
    }

    internal static ConfiguredInstrument Instrument(string symbol) => Settings().GetConfiguredInstruments().Single(item => item.Symbol == symbol);
    private static IbkrBrokerService Broker(bool readOnly = false) => new(new IbkrOptions { ReadOnlyDiagnostics = readOnly },
        NullLogger<IbkrBrokerService>.Instance, tradingSettings: Options.Create(Settings()));
    private static ContractDetails Details(ConfiguredInstrument instrument)
    {
        var contract = IbkrContractFactory.Create(instrument);
        contract.ConId = 12345;
        contract.PrimaryExch = instrument.SecurityType == "STK" ? "ARCA" : "";
        return new ContractDetails
        {
            Contract = contract, MinTick = 0.00005, MinSize = 20000, SizeIncrement = 1,
            SuggestedSizeIncrement = 1000, ValidExchanges = instrument.Exchange,
            MarketRuleIds = "239", TimeZoneId = "UTC", TradingHours = "20261023:0000-20261023:2100"
        };
    }

    private static Task<IbkrContractProbe> AttachRequest(IbkrBrokerService broker, int id, ConfiguredInstrument instrument)
    {
        var requestType = typeof(IbkrBrokerService).GetNestedType("ContractDetailsRequest", BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(requestType, instrument)!;
        var requests = typeof(IbkrBrokerService).GetField("_contractRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(broker)!;
        Assert.Equal(true, requests.GetType().GetMethod("TryAdd")!.Invoke(requests, [id, request]));
        return ((TaskCompletionSource<IbkrContractProbe>)requestType.GetProperty("Completion")!.GetValue(request)!).Task;
    }
}
