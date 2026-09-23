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
        private readonly IInstrumentRegistryService _instrumentRegistry;

        public TradingExecutionGuard(
            IOptions<TradingSettings> tradingSettings,
            IOperatingModeService operatingModeService,
            ITradingEngineStatusService statusService,
            IInstrumentRegistryService instrumentRegistry)
        {
            _tradingSettings = tradingSettings.Value;
            _operatingModeService = operatingModeService;
            _statusService = statusService;
            _instrumentRegistry = instrumentRegistry ?? throw new ArgumentNullException(nameof(instrumentRegistry));
        }

        public async Task<TradingExecutionGuardResult> CanSubmitBrokerOrderAsync(
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

                await ValidateInstrumentReadinessAsync(riskDecision, live: false, reasons, cancellationToken).ConfigureAwait(false);
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

                await ValidateInstrumentReadinessAsync(riskDecision, live: true, reasons, cancellationToken).ConfigureAwait(false);
            }

            return reasons.Count == 0
                ? TradingExecutionGuardResult.Approve()
                : TradingExecutionGuardResult.Reject(reasons);
        }

        private async Task ValidateInstrumentReadinessAsync(
            RiskDecision? riskDecision,
            bool live,
            List<string> reasons,
            CancellationToken cancellationToken)
        {
            var instrumentId = riskDecision?.Context?.InstrumentId;
            if (string.IsNullOrWhiteSpace(instrumentId))
            {
                reasons.Add("RiskDecision has no instrument registry context.");
                return;
            }

            var instrument = await _instrumentRegistry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false);
            if (instrument == null)
            {
                reasons.Add($"Instrument '{instrumentId}' is not registered.");
                return;
            }

            var allowed = live ? instrument.CanSubmitLiveOrders : instrument.CanSubmitPaperOrders;
            if (!allowed)
            {
                reasons.Add(
                    $"Instrument '{instrument.InstrumentId}' is not ready for {(live ? "live" : "paper")} orders " +
                    $"(status={instrument.Status}, configuredEnabled={instrument.ConfiguredEnabled}, tradingRequested={instrument.TradingRequested}).");
            }
        }
    }
}
