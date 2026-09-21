using System;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradingEngineStatusService : ITradingEngineStatusService
    {
        private readonly object _sync = new();
        private BrokerReconciliationStatus _current = new()
        {
            State = TradingEngineState.Starting,
            TradingEnabled = false,
            Message = "Application starting."
        };

        public BrokerReconciliationStatus Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public void SetState(
            TradingEngineState state,
            bool tradingEnabled,
            string message,
            IReadOnlyList<string>? mismatches = null,
            BrokerEnvironmentVerificationStatus brokerEnvironmentVerification = BrokerEnvironmentVerificationStatus.Unknown,
            string? connectedAccountId = null,
            bool reconciliationCompleted = false)
        {
            lock (_sync)
            {
                _current = new BrokerReconciliationStatus
                {
                    State = state,
                    TradingEnabled = tradingEnabled && state == TradingEngineState.Ready,
                    ReconciliationCompleted = reconciliationCompleted,
                    BrokerEnvironmentVerification = brokerEnvironmentVerification,
                    ConnectedAccountId = connectedAccountId ?? _current.ConnectedAccountId,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Message = message ?? string.Empty,
                    Mismatches = mismatches ?? Array.Empty<string>()
                };
            }
        }
    }
}
