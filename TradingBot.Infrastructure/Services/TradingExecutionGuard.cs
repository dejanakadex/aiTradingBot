using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services
{
    public sealed class TradingExecutionGuard : ITradingExecutionGuard
    {
        private readonly TradingSettings _tradingSettings;
        private readonly IOperatingModeService _operatingModeService;
        private readonly ITradingEngineStatusService _statusService;

        public TradingExecutionGuard(
            IOptions<TradingSettings> tradingSettings,
            IOperatingModeService operatingModeService,
            ITradingEngineStatusService statusService)
        {
            _tradingSettings = tradingSettings.Value;
            _operatingModeService = operatingModeService;
            _statusService = statusService;
        }

        public Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(
            RiskDecision? riskDecision,
            CancellationToken cancellationToken = default)
        {
            var reasons = new List<string>();
            var status = _statusService.Current;
            var mode = _operatingModeService.CurrentMode;

            if (!_tradingSettings.Enabled)
            {
                reasons.Add("TradingSettings.Enabled is false.");
            }

            if (status.State != TradingEngineState.Ready)
            {
                reasons.Add($"Trading engine state is {status.State}, not Ready.");
            }

            if (!status.TradingEnabled)
            {
                reasons.Add("Trading engine trading flag is disabled.");
            }

            if (!status.ReconciliationCompleted)
            {
                reasons.Add("Startup reconciliation has not completed successfully.");
            }

            if (riskDecision == null || riskDecision.Decision != RiskDecisionType.Approve || riskDecision.ApprovedQuantity <= 0m)
            {
                reasons.Add("RiskDecision is not approved.");
            }

            if (mode == TradingOperatingMode.AnalysisOnly)
            {
                reasons.Add("AnalysisOnly mode never submits broker orders.");
            }
            else if (mode == TradingOperatingMode.PaperTrading)
            {
                if (status.BrokerEnvironmentVerification != BrokerEnvironmentVerificationStatus.VerifiedPaper)
                {
                    reasons.Add($"Broker environment is {status.BrokerEnvironmentVerification}, not VerifiedPaper.");
                }
            }
            else if (mode == TradingOperatingMode.LiveTrading)
            {
                if (!_tradingSettings.LiveTradingExplicitlyEnabled)
                {
                    reasons.Add("LiveTradingExplicitlyEnabled is false.");
                }

                if (status.BrokerEnvironmentVerification != BrokerEnvironmentVerificationStatus.VerifiedLive)
                {
                    reasons.Add($"Broker environment is {status.BrokerEnvironmentVerification}, not VerifiedLive.");
                }
            }

            return Task.FromResult(reasons.Count == 0
                ? TradingExecutionGuardResult.Approve()
                : TradingExecutionGuardResult.Reject(reasons));
        }
    }
}
