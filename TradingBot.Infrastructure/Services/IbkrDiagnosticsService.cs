using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Infrastructure.Services;

public sealed record IbkrInstrumentDiagnostics(
    string InstrumentId,
    string Symbol,
    string ContractConfiguration,
    string BrokerContractStatus,
    int? BrokerContractId,
    string? BrokerPrimaryExchange,
    int? ContractErrorCode,
    string MarketDataStatus,
    int? MarketDataErrorCode,
    DateTime? LastQuoteUtc,
    IReadOnlyList<IbkrStreamDiagnostics> CollectionStreams);

public sealed record IbkrStreamDiagnostics(
    string Timeframe,
    MarketDataCollectionStatus Status,
    bool BrokerConnected,
    bool Subscribed,
    DateTime? LastHeartbeatUtc,
    DateTime? LastEventTimeUtc,
    long? ReceiveLagMilliseconds);

public sealed record IbkrDiagnosticsSnapshot(
    string AdapterStatus,
    string? ApiVersion,
    int? ServerVersion,
    string ConnectionStatus,
    bool BrokerConnected,
    string OperatingMode,
    string HostType,
    int Port,
    bool HostConfigured,
    bool PaperAccountConfigured,
    string AccountStatus,
    IReadOnlyList<IbkrInstrumentDiagnostics> Instruments);

/// <summary>Combines independent adapter, broker, account, contract and feed observations.</summary>
public sealed class IbkrDiagnosticsService
{
    private readonly IIbkrAdapter _adapter;
    private readonly IIbkrConnectionService _connection;
    private readonly IbkrSettings _ibkr;
    private readonly TradingSettings _trading;
    private readonly IMarketDataCollectionStatusService _collection;
    private readonly ILatestMarketDataService _latest;

    public IbkrDiagnosticsService(
        IIbkrAdapter adapter,
        IIbkrConnectionService connection,
        IOptions<IbkrSettings> ibkr,
        IOptions<TradingSettings> trading,
        IMarketDataCollectionStatusService collection,
        ILatestMarketDataService latest)
    {
        _adapter = adapter;
        _connection = connection;
        _ibkr = ibkr.Value;
        _trading = trading.Value;
        _collection = collection;
        _latest = latest;
    }

    public async Task<IbkrDiagnosticsSnapshot> GetAsync(string? probeInstrumentId = null, CancellationToken cancellationToken = default)
    {
        var configured = _trading.GetConfiguredInstruments();
        if (probeInstrumentId is not null && !configured.Any(item => item.InstrumentId.Equals(probeInstrumentId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Unknown configured instrument.", nameof(probeInstrumentId));

        var runtime = _adapter as IIbkrRuntimeDiagnostics;
#if IBKR_API_AVAILABLE
        var real = _adapter is IbkrBrokerService;
#else
        const bool real = false;
#endif
        var adapterStatus = _adapter is UnavailableIbkrAdapter ? "Unavailable" : real ? "Real" : "Custom";
        var connected = _adapter.IsConnected && _connection.Status == ConnectionStatus.Connected;
        var accountStatus = GetAccountStatus(runtime, connected);
        var streams = _collection.GetAll();
        var nowUtc = DateTime.UtcNow;
        var instruments = new List<IbkrInstrumentDiagnostics>(configured.Count);

        foreach (var instrument in configured)
        {
            var supported = IsSupportedContract(instrument);
            var contractStatus = supported ? "NotProbed" : "UnsupportedByAdapter";
            int? contractId = null;
            string? primaryExchange = null;
            int? contractError = null;
            if (probeInstrumentId is not null && instrument.InstrumentId.Equals(probeInstrumentId, StringComparison.OrdinalIgnoreCase))
            {
                if (!supported) contractStatus = "UnsupportedByAdapter";
                else if (!connected || runtime is null) contractStatus = "BrokerUnavailable";
                else
                {
                    try
                    {
                        var result = await runtime.ProbeContractAsync(instrument, cancellationToken).ConfigureAwait(false);
                        contractStatus = result.Status;
                        contractId = result.ContractId;
                        primaryExchange = result.PrimaryExchange;
                        contractError = result.ErrorCode;
                    }
                    catch (InvalidOperationException)
                    {
                        contractStatus = "BrokerUnavailable";
                    }
                }
            }

            var instrumentStreams = streams.Where(item => item.InstrumentId.Equals(instrument.InstrumentId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var quote = _latest.Get(instrument.InstrumentId);
            var lastQuote = new[] { quote?.BidTimeUtc, quote?.AskTimeUtc }.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
            DateTime? lastQuoteUtc = lastQuote == default ? null : lastQuote;
            var marketError = runtime?.GetMarketDataErrorCode(instrument.Symbol);
            string marketStatus;
            if (!instrument.Enabled) marketStatus = "Disabled";
            else if (!connected) marketStatus = "BrokerDisconnected";
            else if (marketError.HasValue) marketStatus = "BrokerError";
            else if (instrumentStreams.Length == 0) marketStatus = "NotSubscribed";
            else if (instrumentStreams.Any(item => item.Status is MarketDataCollectionStatus.Faulted or MarketDataCollectionStatus.Stale)) marketStatus = "Unhealthy";
            else if (instrumentStreams.Any(item => item.Status == MarketDataCollectionStatus.IdleOutsideSession)) marketStatus = "IdleOutsideSession";
            else if (quote?.BidTimeUtc is null || quote.AskTimeUtc is null) marketStatus = "NoBidAsk";
            else if (nowUtc - quote.BidTimeUtc.Value > TimeSpan.FromSeconds(_trading.MaximumQuoteAgeSeconds)
                || nowUtc - quote.AskTimeUtc.Value > TimeSpan.FromSeconds(_trading.MaximumQuoteAgeSeconds)) marketStatus = "StaleBidAsk";
            else if (instrumentStreams.Any(item => item.Status != MarketDataCollectionStatus.Live)) marketStatus = "WaitingForStream";
            else marketStatus = "Available";

            instruments.Add(new IbkrInstrumentDiagnostics(instrument.InstrumentId, instrument.Symbol,
                supported ? "Supported" : "Requires STK/SMART/USD adapter support", contractStatus,
                contractId, primaryExchange, contractError, marketStatus, marketError, lastQuoteUtc,
                instrumentStreams.Select(item => new IbkrStreamDiagnostics(item.Timeframe, item.Status, item.BrokerConnected,
                    item.Subscribed, item.LastHeartbeatUtc, item.LastEventTimeUtc, item.ReceiveLagMilliseconds)).ToArray()));
        }

        return new IbkrDiagnosticsSnapshot(adapterStatus, runtime?.ApiVersion, connected ? runtime?.ServerVersion : null,
            _connection.Status.ToString(), connected, _trading.OperatingMode.ToString(), _ibkr.HostType.ToString(),
            _ibkr.GetPort(_trading.OperatingMode), !string.IsNullOrWhiteSpace(_ibkr.Host),
            !string.IsNullOrWhiteSpace(_ibkr.PaperAccountId), accountStatus, instruments);
    }

    private string GetAccountStatus(IIbkrRuntimeDiagnostics? runtime, bool connected)
    {
        if (_trading.OperatingMode == TradingOperatingMode.PaperTrading && string.IsNullOrWhiteSpace(_ibkr.PaperAccountId)) return "PaperAccountNotConfigured";
        if (!connected || runtime is null) return "BrokerUnavailable";
        if (!runtime.ManagedAccountsReceived) return "AwaitingManagedAccounts";
        return runtime.ConfiguredAccountRecognized switch
        {
            true => "Confirmed",
            false => "Mismatch",
            _ => "AccountNotConfigured"
        };
    }

    private static bool IsSupportedContract(ConfiguredInstrument instrument) =>
        instrument.SecurityType.Equals("STK", StringComparison.OrdinalIgnoreCase)
        && instrument.Exchange.Equals("SMART", StringComparison.OrdinalIgnoreCase)
        && instrument.Currency.Equals("USD", StringComparison.OrdinalIgnoreCase);
}
