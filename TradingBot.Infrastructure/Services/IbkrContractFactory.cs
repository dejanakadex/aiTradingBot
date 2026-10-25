using IBApi;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Infrastructure.Services;

/// <summary>Maps normalized configuration to broker contracts; this grants no data or order permission.</summary>
public static class IbkrContractFactory
{
    public static bool IsSupported(ConfiguredInstrument instrument) => instrument.InstrumentType switch
    {
        InstrumentType.Stock => Equal(instrument.Exchange, "SMART") && Equal(instrument.Currency, "USD")
            && !string.IsNullOrWhiteSpace(instrument.Symbol),
        InstrumentType.Forex => Equal(instrument.Exchange, "IDEALPRO") && instrument.BaseCurrency is not null,
        _ => false
    };

    public static Contract Create(ConfiguredInstrument instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        if (!IsSupported(instrument))
            throw new NotSupportedException("Contract requires STK/SMART/USD or a valid CASH currency pair on IDEALPRO.");
        return new Contract
        {
            Symbol = InstrumentIdentity.Normalize(instrument.BrokerSymbol),
            SecType = InstrumentIdentity.Normalize(instrument.SecurityType),
            Exchange = InstrumentIdentity.Normalize(instrument.Exchange),
            Currency = InstrumentIdentity.Normalize(instrument.Currency)
        };
    }

    public static IbkrContractIdentity Describe(ConfiguredInstrument instrument)
    {
        var contract = Create(instrument);
        return new(contract.Symbol, contract.SecType, contract.Exchange, contract.Currency);
    }

    public static IbkrContractProbe Evaluate(ConfiguredInstrument instrument, IReadOnlyList<ContractDetails> details)
    {
        var expected = Create(instrument);
        if (details.Count != 1)
            return new IbkrContractProbe(details.Count == 0 ? "NotFound" : "Ambiguous") { MatchCount = details.Count };

        var detail = details[0];
        var actual = detail.Contract;
        // A venue listed among valid exchanges is not proof that this response resolved the requested venue.
        var matches = actual?.ConId > 0 && Equal(actual.Symbol, expected.Symbol)
            && Equal(actual.SecType, expected.SecType) && Equal(actual.Currency, expected.Currency)
            && Equal(actual.Exchange, expected.Exchange);
        var metadata = actual is null ? null : new IbkrContractMetadata(
            new(actual.Symbol, actual.SecType, actual.Exchange, actual.Currency),
            actual.LocalSymbol, detail.ValidExchanges, Positive(detail.MinTick),
            Positive(detail.MinSize), Positive(detail.SizeIncrement), Positive(detail.SuggestedSizeIncrement),
            detail.MarketRuleIds, detail.TimeZoneId, detail.TradingHours, detail.LiquidHours);
        return new IbkrContractProbe(matches ? "Confirmed" : "Mismatch", actual?.ConId, actual?.PrimaryExch)
        {
            MatchCount = 1,
            Metadata = metadata
        };
    }

    private static bool Equal(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static decimal? Positive(decimal value) => value > 0 && value < decimal.MaxValue ? value : null;
    private static decimal? Positive(double value) => double.IsFinite(value) && value > 0 && value < (double)decimal.MaxValue
        ? (decimal)value : null;
}
