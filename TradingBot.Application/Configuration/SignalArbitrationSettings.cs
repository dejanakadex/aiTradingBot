using TradingBot.Domain.Enums;

namespace TradingBot.Application.Configuration
{
    public sealed class SignalArbitrationSettings
    {
        public SignalConflictPolicy ConflictPolicy { get; set; } = SignalConflictPolicy.Reject;
        public bool AllowSameDirectionScaleIn { get; set; } = true;
        public bool RejectSameStrategyWhileActive { get; set; } = true;
        public int MaximumActiveAllocationsPerInstrument { get; set; } = 3;
        public int IntentTimeoutSeconds { get; set; } = 120;
        public Dictionary<string, int> StrategyPriorities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
