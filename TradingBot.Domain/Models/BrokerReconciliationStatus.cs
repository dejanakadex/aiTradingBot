using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class BrokerReconciliationStatus
    {
        public TradingEngineState State { get; init; } = TradingEngineState.Starting;
        public bool TradingEnabled { get; init; }
        public bool ReconciliationCompleted { get; init; }
        public BrokerEnvironmentVerificationStatus BrokerEnvironmentVerification { get; init; } = BrokerEnvironmentVerificationStatus.Unknown;
        public string ConnectedAccountId { get; init; } = string.Empty;
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<string> Mismatches { get; init; } = Array.Empty<string>();
    }
}
