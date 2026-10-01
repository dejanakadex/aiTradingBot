using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure.Interfaces;

public sealed record IbkrContractProbe(string Status, int? ContractId = null, string? PrimaryExchange = null, int? ErrorCode = null);

/// <summary>Read-only broker observations and an optional contract-details request. No order methods.</summary>
public interface IIbkrRuntimeDiagnostics
{
    string ApiVersion { get; }
    int? ServerVersion { get; }
    bool ManagedAccountsReceived { get; }
    bool? ConfiguredAccountRecognized { get; }
    int? GetMarketDataErrorCode(string symbol);
    Task<IbkrContractProbe> ProbeContractAsync(ConfiguredInstrument instrument, CancellationToken cancellationToken = default);
}
