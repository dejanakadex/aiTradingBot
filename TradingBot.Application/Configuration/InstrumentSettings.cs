using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Configuration
{
    public sealed class InstrumentSettings
    {
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = "SMART";
        public string Currency { get; set; } = "USD";
        public string SecurityType { get; set; } = "STK";
        public bool Enabled { get; set; } = true;
        public bool TradingEnabled { get; set; } = false;
        public TradeDirection[] AllowedDirections { get; set; } = new[] { TradeDirection.Long };
        public string[] StrategyIds { get; set; } = new[] { PipelineContractVersions.DefaultStrategyId };
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
        int? MaximumHoldingSeconds);
}
