using TradingBot.Application.Configuration;

namespace TradingBot.Infrastructure.Interfaces;

public sealed record IbkrContractIdentity(string Symbol, string SecurityType, string Exchange, string Currency);

/// <summary>Broker observations, not validated execution rules. Missing/sentinel sizes remain unknown.</summary>
public sealed record IbkrContractMetadata(
    IbkrContractIdentity Contract, string? LocalSymbol, string? ValidExchanges,
    decimal? MinimumTick, decimal? MinimumSize, decimal? SizeIncrement, decimal? SuggestedSizeIncrement,
    string? MarketRuleIds, string? TimeZoneId, string? TradingHours, string? LiquidHours);

public sealed record IbkrContractProbe(string Status, int? ContractId = null, string? PrimaryExchange = null, int? ErrorCode = null)
{
    public int? MatchCount { get; init; }
    public IbkrContractMetadata? Metadata { get; init; }
}

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
