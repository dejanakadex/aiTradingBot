using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ITradingEngineStatusService
    {
        BrokerReconciliationStatus Current { get; }

        void SetState(
            TradingEngineState state,
            bool tradingEnabled,
            string message,
            IReadOnlyList<string>? mismatches = null,
            BrokerEnvironmentVerificationStatus brokerEnvironmentVerification = BrokerEnvironmentVerificationStatus.Unknown,
            string? connectedAccountId = null,
            bool reconciliationCompleted = false);
    }
}
