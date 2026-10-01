using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Services;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class IbkrDiagnosticsServiceTests
{
    [Fact]
    public async Task MissingAdapterSeparatesCompilationFromBrokerAndFeed()
    {
        var adapter = new UnavailableIbkrAdapter(NullLogger<UnavailableIbkrAdapter>.Instance);
        var connection = new FakeIbkrConnectionService();
        var result = await Create(adapter, connection, out _, out _).GetAsync();

        Assert.Equal("Unavailable", result.AdapterStatus);
        Assert.False(result.BrokerConnected);
        Assert.Equal("PaperAccountNotConfigured", result.AccountStatus);
        var instrument = Assert.Single(result.Instruments);
        Assert.Equal("NotProbed", instrument.BrokerContractStatus);
        Assert.Equal("BrokerDisconnected", instrument.MarketDataStatus);
    }

    [Fact]
    public async Task ConnectedDiagnosticsConfirmAccountContractAndQuotesWithoutExposingAccountId()
    {
        var adapter = new DiagnosticAdapter { IsConnected = true, ManagedAccountsReceived = true, ConfiguredAccountRecognized = true };
        var connection = new FakeIbkrConnectionService();
        await connection.ConnectAsync();
        var service = Create(adapter, connection, out var collection, out var latest, "DU_TEST_ACCOUNT");
        var now = DateTime.UtcNow;
        collection.EnsureStream("SPY:1m", "SPY", "SPY", "1m", 120, now);
        collection.ReportHeartbeat("SPY:1m", now, now, 0);
        latest.Apply(Quote(MarketDataEventKind.Bid, 100m, now));
        latest.Apply(Quote(MarketDataEventKind.Ask, 100.02m, now));

        var result = await service.GetAsync("SPY");

        Assert.Equal("Custom", result.AdapterStatus); // A test adapter is not the compiled IBKR implementation.
        Assert.Equal("Confirmed", result.AccountStatus);
        Assert.Equal("Available", Assert.Single(result.Instruments).MarketDataStatus);
        Assert.Equal("Confirmed", Assert.Single(result.Instruments).BrokerContractStatus);
        Assert.Equal(12345, Assert.Single(result.Instruments).BrokerContractId);
        Assert.Equal(1, adapter.ProbeCalls);
        Assert.DoesNotContain("DU_TEST_ACCOUNT", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task NoProbeDoesNotCallBrokerAndFeedErrorIsDistinctFromConnection()
    {
        var adapter = new DiagnosticAdapter { IsConnected = true, ManagedAccountsReceived = true, ConfiguredAccountRecognized = false, MarketDataErrorCode = 354 };
        var connection = new FakeIbkrConnectionService();
        await connection.ConnectAsync();
        var service = Create(adapter, connection, out _, out _, "DU_TEST_ACCOUNT");

        var result = await service.GetAsync();

        Assert.Equal("Mismatch", result.AccountStatus);
        Assert.Equal("BrokerError", Assert.Single(result.Instruments).MarketDataStatus);
        Assert.Equal(354, Assert.Single(result.Instruments).MarketDataErrorCode);
        Assert.Equal("NotProbed", Assert.Single(result.Instruments).BrokerContractStatus);
        Assert.Equal(0, adapter.ProbeCalls);
    }

    private static IbkrDiagnosticsService Create(
        IIbkrAdapter adapter, IIbkrConnectionService connection,
        out MarketDataCollectionStatusService collection,
        out LatestMarketDataService latest, string? paperAccount = null)
    {
        collection = new MarketDataCollectionStatusService();
        latest = new LatestMarketDataService();
        var trading = new TradingSettings
        {
            OperatingMode = TradingOperatingMode.PaperTrading,
            Instruments = [new InstrumentSettings { InstrumentId = "SPY", Symbol = "SPY", TradingEnabled = false }]
        };
        return new IbkrDiagnosticsService(adapter, connection,
            Options.Create(new IbkrSettings { PaperAccountId = paperAccount }),
            Options.Create(trading), collection, latest);
    }

    private static CanonicalMarketDataEvent Quote(MarketDataEventKind kind, decimal price, DateTime now) => new()
    {
        EventId = $"test-{kind}", InstrumentId = "SPY", Symbol = "SPY", Kind = kind,
        EventTimeUtc = now, ReceivedTimeUtc = now, Price = price, Source = "Test", IsFinal = true
    };

    private sealed class DiagnosticAdapter : IIbkrAdapter, IIbkrRuntimeDiagnostics
    {
        public bool IsConnected { get; set; }
        public string ApiVersion => "test";
        public int? ServerVersion => 200;
        public bool ManagedAccountsReceived { get; set; }
        public bool? ConfiguredAccountRecognized { get; set; }
        public int? MarketDataErrorCode { get; set; }
        public int ProbeCalls { get; private set; }
        public int? GetMarketDataErrorCode(string symbol) => MarketDataErrorCode;
        public Task<IbkrContractProbe> ProbeContractAsync(ConfiguredInstrument instrument, CancellationToken cancellationToken = default)
        {
            ProbeCalls++;
            return Task.FromResult(new IbkrContractProbe("Confirmed", 12345, "ARCA"));
        }
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
#pragma warning disable CS0067
        public event Func<ConnectionStatus, Task>? ConnectionStatusChanged;
        public event Func<bool, Task>? ReadinessChanged;
        public event Func<MarketBar, Task>? MarketBarReceived;
        public event Func<CanonicalMarketDataEvent, Task>? MarketDataEventReceived;
#pragma warning restore CS0067
    }
}
