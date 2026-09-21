using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class TradeSetupCandidate
    {
        public TradeSetupCandidate(
            PatternCandidate pattern,
            PatternQualityBreakdown quality,
            IEnumerable<string>? gateReasons = null)
        {
            Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            Quality = quality ?? throw new ArgumentNullException(nameof(quality));
            GateReasons = new List<string>(gateReasons ?? Array.Empty<string>());
        }

        public PatternCandidate Pattern { get; }
        public PatternQualityBreakdown Quality { get; }
        public IReadOnlyList<string> GateReasons { get; }
    }
}
