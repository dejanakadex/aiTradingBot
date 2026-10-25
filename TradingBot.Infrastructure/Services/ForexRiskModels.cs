using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;

namespace TradingBot.Infrastructure.Services;

public sealed record CurrencyAmount(decimal Amount, string Currency);
/// <summary>USD per one unit of Currency, with both market and observation time. No implicit 1:1 conversion.</summary>
public sealed record UsdConversionQuote(string Currency, decimal Bid, decimal Ask, DateTime EventUtc, DateTime ReceivedUtc)
{
    public static UsdConversionQuote FromPair(string basis, string quote, decimal bid, decimal ask, DateTime eventUtc, DateTime receivedUtc)
    {
        if (bid <= 0 || ask < bid || basis == quote) throw new ArgumentException("Invalid conversion quote.");
        if (quote == "USD") return new(basis, bid, ask, eventUtc, receivedUtc);
        if (basis == "USD") return new(quote, 1m / ask, 1m / bid, eventUtc, receivedUtc);
        throw new ArgumentException("Conversion requires a direct or inverse USD pair; no implicit cross-rate chain.");
    }
}
public sealed record ForexPriceBand(decimal LowEdge, decimal Increment);
/// <summary>Reviewed execution constraints, separate from indicative contract-details metadata.</summary>
public sealed record ForexOrderRules(int ContractId, DateTime VerifiedAtUtc, string EvidenceSource,
    decimal MinimumBaseUnits, decimal MaximumBaseUnits, decimal BaseUnitIncrement,
    int? MarketRuleId, IReadOnlyList<ForexPriceBand> PriceBands);

/// <summary>One marked position or unrepresented pending reservation, in its valuation currency.
/// FX legs include both signed currency amounts; a stock position supplies its valuation-currency leg.
/// Caller must de-duplicate broker positions, open orders and committed reservations.</summary>
public sealed record ForexExposure(string InstrumentId, string StrategyId, CurrencyAmount SignedNotional,
    IReadOnlyList<CurrencyAmount> CurrencyLegs);
/// <summary>AvailableBuyingPower is already net of all pending commitments. Exposures include them once.
/// It is a conservative full-notional budget, not a computed leveraged FX margin allowance.</summary>
public sealed record ForexPortfolioSnapshot(DateTime AsOfUtc, bool Reconciled,
    CurrencyAmount NetLiquidation, CurrencyAmount AvailableBuyingPower, CurrencyAmount DailyRealizedPnl,
    IReadOnlyList<ForexExposure> Exposures, int OpenPositionAndOrderCount);

public sealed record ForexRiskRequest(string InstrumentId, string StrategyId, TradeDirection Direction,
    DateTime AsOfUtc, decimal Bid, decimal Ask, DateTime QuoteEventUtc, DateTime QuoteReceivedUtc,
    decimal StopPrice, decimal TargetPrice, decimal ExpectedMidpointMove, decimal MaximumBaseUnits,
    IbkrContractProbe Contract, DateTime ContractObservedUtc, ForexOrderRules? Rules,
    ForexPortfolioSnapshot Portfolio, IReadOnlyList<UsdConversionQuote> Conversions);

/// <summary>Read-only sizing assessment. Passing is never an order/reservation approval.</summary>
public sealed record ForexRiskAssessment
{
    public bool Passed => Reasons.Count == 0;
    public bool ExecutionAuthorized => false;
    public string Version { get; init; } = "fx-risk-v1";
    public string InputHash { get; init; } = "";
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public string? BaseCurrency { get; init; }
    public string? QuoteCurrency { get; init; }
    public decimal BaseUnits { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal StopPrice { get; init; }
    public decimal TargetPrice { get; init; }
    public decimal PipValueUsd { get; init; }
    public decimal NotionalUsd { get; init; }
    public decimal StopRiskUsd { get; init; }
    public decimal CommissionUsd { get; init; }
    public decimal SpreadUsd { get; init; }
    public decimal SlippageUsd { get; init; }
    public decimal TotalCostUsd { get; init; }
    public decimal ExpectedGrossProfitUsd { get; init; }
    public decimal ExpectedNetProfitUsd { get; init; }
    public decimal ExpectedNetEdgeBps { get; init; }
    public IReadOnlyDictionary<string, decimal> CurrencyLegsUsd { get; init; } = new Dictionary<string, decimal>();
}
