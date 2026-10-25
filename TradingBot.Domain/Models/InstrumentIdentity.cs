using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Instrument identity only; broker entitlement and contract support are verified separately.</summary>
public static class InstrumentIdentity
{
    public static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    public static InstrumentType GetInstrumentType(string? securityType) => Normalize(securityType) switch
    {
        "STK" => InstrumentType.Stock,
        "CASH" => InstrumentType.Forex,
        _ => InstrumentType.Unknown
    };

    public static bool IsCurrencyCode(string? value)
    {
        var code = Normalize(value);
        return code.Length == 3 && code.All(c => c is >= 'A' and <= 'Z');
    }

    public static string NormalizeForexSymbol(string? symbol)
    {
        var value = Normalize(symbol);
        return value.Length == 7 && value[3] is '/' or '.' ? value.Remove(3, 1) : value;
    }

    public static string? GetBaseCurrency(string? securityType, string? symbol, string? quoteCurrency)
    {
        if (GetInstrumentType(securityType) != InstrumentType.Forex) return null;
        var pair = Normalize(symbol);
        var quote = Normalize(quoteCurrency);
        return pair.Length == 6 && IsCurrencyCode(pair[..3]) && IsCurrencyCode(quote)
            && pair.EndsWith(quote, StringComparison.Ordinal) && pair[..3] != quote
            ? pair[..3] : null;
    }

    public static string ContractKey(string securityType, string symbol, string exchange, string currency) =>
        string.Join('|', Normalize(securityType), Normalize(symbol), Normalize(exchange), Normalize(currency));
}
