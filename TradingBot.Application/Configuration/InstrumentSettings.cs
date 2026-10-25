using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Configuration
{
    public sealed class InstrumentSettings
    {
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        // Required for CASH. Currency is the quote currency; Symbol is the internal pair, not the broker symbol.
        public string? BaseCurrency { get; set; }
        public string Exchange { get; set; } = "SMART";
        public string Currency { get; set; } = "USD";
        public string SecurityType { get; set; } = "STK";
        public bool Enabled { get; set; } = true;
        public bool TradingEnabled { get; set; } = false;
        // Apply omitted defaults after binding: the configuration binder appends to initialized arrays.
        public TradeDirection[]? AllowedDirections { get; set; }
        public string[]? StrategyIds { get; set; }
        public string[] MarketDataTimeframes { get; set; } = Array.Empty<string>();
        public decimal? MaximumPositionValue { get; set; }
        public int? MaximumHoldingSeconds { get; set; }
    }

    public sealed record ConfiguredInstrument(
        string InstrumentId,
        string Symbol,
        string Exchange,
        string Currency,
        string SecurityType,
        bool Enabled,
        bool TradingEnabled,
        IReadOnlyList<TradeDirection> AllowedDirections,
        IReadOnlyList<string> StrategyIds,
        IReadOnlyList<string> MarketDataTimeframes,
        decimal? MaximumPositionValue,
        int? MaximumHoldingSeconds)
    {
        public InstrumentType InstrumentType => InstrumentIdentity.GetInstrumentType(SecurityType);
        public string? BaseCurrency => InstrumentIdentity.GetBaseCurrency(SecurityType, Symbol, Currency);
        public string QuoteCurrency => Currency;
        public string BrokerSymbol => InstrumentType == InstrumentType.Forex
            ? BaseCurrency ?? throw new InvalidOperationException("Invalid normalized forex identity.") : Symbol;
        public string DisplaySymbol => BaseCurrency is { } basis ? $"{basis}/{Currency}" : Symbol;
        public string ContractKey => InstrumentIdentity.ContractKey(SecurityType, Symbol, Exchange, Currency);
    }
}
