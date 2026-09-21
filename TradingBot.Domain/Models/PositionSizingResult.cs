using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class PositionSizingResult
    {
        public decimal Quantity { get; init; }
        public decimal PositionValue { get; init; }
        public decimal RiskAmount { get; init; }
        public IReadOnlyList<string> RejectionReasons { get; init; } = System.Array.Empty<string>();
    }
}
