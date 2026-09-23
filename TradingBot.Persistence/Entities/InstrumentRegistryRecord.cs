using TradingBot.Domain.Enums;

namespace TradingBot.Persistence
{
    public sealed class InstrumentRegistryRecord
    {
        public int Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Exchange { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
        public string SecurityType { get; set; } = string.Empty;
        public bool ConfiguredEnabled { get; set; }
        public bool TradingRequested { get; set; }
        public InstrumentOnboardingStatus Status { get; set; }
        public string AllowedDirectionsJson { get; set; } = "[]";
        public string StrategyIdsJson { get; set; } = "[]";
        public string MarketDataTimeframesJson { get; set; } = "[]";
        public decimal? MaximumPositionValue { get; set; }
        public int? MaximumHoldingSeconds { get; set; }
        public long? BrokerContractId { get; set; }
        public string BrokerPrimaryExchange { get; set; } = string.Empty;
        public string ConfigurationHash { get; set; } = string.Empty;
        public string StatusReason { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime StatusChangedAtUtc { get; set; }
        public int Version { get; set; }
    }

    public sealed class InstrumentStatusTransitionRecord
    {
        public long Id { get; set; }
        public string InstrumentId { get; set; } = string.Empty;
        public InstrumentOnboardingStatus? FromStatus { get; set; }
        public InstrumentOnboardingStatus ToStatus { get; set; }
        public string Reason { get; set; } = string.Empty;
        public string Trigger { get; set; } = string.Empty;
        public DateTime TimestampUtc { get; set; }
    }
}
