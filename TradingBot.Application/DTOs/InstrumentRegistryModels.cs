using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public sealed record InstrumentRegistrySnapshot
    {
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string Exchange { get; init; } = string.Empty;
        public string Currency { get; init; } = string.Empty;
        public string SecurityType { get; init; } = string.Empty;
        public bool ConfiguredEnabled { get; init; }
        public bool TradingRequested { get; init; }
        public InstrumentOnboardingStatus Status { get; init; }
        public IReadOnlyList<TradeDirection> AllowedDirections { get; init; } = Array.Empty<TradeDirection>();
        public IReadOnlyList<string> StrategyIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> MarketDataTimeframes { get; init; } = Array.Empty<string>();
        public decimal? MaximumPositionValue { get; init; }
        public int? MaximumHoldingSeconds { get; init; }
        public long? BrokerContractId { get; init; }
        public string BrokerPrimaryExchange { get; init; } = string.Empty;
        public string StatusReason { get; init; } = string.Empty;
        public DateTime CreatedAtUtc { get; init; }
        public DateTime UpdatedAtUtc { get; init; }
        public DateTime StatusChangedAtUtc { get; init; }
        public int Version { get; init; }

        public bool CollectionEnabled => ConfiguredEnabled;
        public bool ResearchDataReady => Status is
            InstrumentOnboardingStatus.ResearchReady or
            InstrumentOnboardingStatus.ShadowReady or
            InstrumentOnboardingStatus.PaperReady or
            InstrumentOnboardingStatus.LiveEnabled or
            InstrumentOnboardingStatus.Suspended;
        public bool PaperTradingReady => Status is InstrumentOnboardingStatus.PaperReady or InstrumentOnboardingStatus.LiveEnabled;
        public bool LiveTradingReady => Status == InstrumentOnboardingStatus.LiveEnabled;
        public bool CanSubmitPaperOrders => ConfiguredEnabled && TradingRequested && PaperTradingReady;
        public bool CanSubmitLiveOrders => ConfiguredEnabled && TradingRequested && LiveTradingReady;
    }

    public sealed record InstrumentRegistrySyncResult(
        int Added,
        int Updated,
        int Disabled,
        int Unchanged,
        IReadOnlyList<InstrumentRegistrySnapshot> Instruments);
}
