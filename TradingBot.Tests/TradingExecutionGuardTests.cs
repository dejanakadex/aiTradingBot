using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Services;
using Xunit;

namespace TradingBot.Tests
{
    public sealed class TradingExecutionGuardTests
    {
        [Fact]
        public async Task AnalysisOnly_NeverAllowsBrokerSubmission()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.AnalysisOnly },
                TradingOperatingMode.AnalysisOnly,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedPaper));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("AnalysisOnly"));
        }

        [Theory]
        [InlineData(TradingOperatingMode.AnalysisOnly)]
        [InlineData(TradingOperatingMode.PaperTrading)]
        [InlineData(TradingOperatingMode.LiveTrading)]
        public async Task EnabledFalse_NeverAllowsBrokerSubmissionRegardlessOfMode(TradingOperatingMode mode)
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = false, OperatingMode = mode, LiveTradingExplicitlyEnabled = true },
                mode,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("Enabled is false"));
        }

        [Fact]
        public async Task PaperTrading_RejectsWhenBrokerEnvironmentCannotBeVerifiedAsPaper()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading },
                TradingOperatingMode.PaperTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.Unknown));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("not VerifiedPaper"));
        }

        [Fact]
        public async Task PaperTrading_ConnectedLiveEnvironmentRejects()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading },
                TradingOperatingMode.PaperTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("not VerifiedPaper"));
        }

        [Fact]
        public async Task LiveTrading_WithoutExplicitLiveGateRejects()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.LiveTrading, LiveTradingExplicitlyEnabled = false },
                TradingOperatingMode.LiveTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("LiveTradingExplicitlyEnabled is false"));
        }

        [Fact]
        public async Task LiveTrading_AllSafetyGatesPass_AllowsBrokerSubmission()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.LiveTrading, LiveTradingExplicitlyEnabled = true },
                TradingOperatingMode.LiveTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive));

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.True(result.Approved);
        }

        [Fact]
        public async Task LiveTrading_InstrumentStillBackfillPending_Rejects()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.LiveTrading, LiveTradingExplicitlyEnabled = true },
                TradingOperatingMode.LiveTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive),
                InstrumentOnboardingStatus.BackfillPending);

            var result = await guard.CanSubmitBrokerOrderAsync(ApprovedRisk());

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, reason => reason.Contains("not ready for live orders"));
        }

        [Fact]
        public async Task UnapprovedRiskDecision_Rejects()
        {
            var guard = CreateGuard(
                new TradingSettings { Enabled = true, OperatingMode = TradingOperatingMode.LiveTrading, LiveTradingExplicitlyEnabled = true },
                TradingOperatingMode.LiveTrading,
                ReadyStatus(BrokerEnvironmentVerificationStatus.VerifiedLive));

            var result = await guard.CanSubmitBrokerOrderAsync(new RiskDecision(RiskDecisionType.Reject, "no", 0m, 0m, 0m, DateTime.UtcNow));

            Assert.False(result.Approved);
            Assert.Contains(result.RejectionReasons, r => r.Contains("RiskDecision is not approved"));
        }

        private static TradingExecutionGuard CreateGuard(
            TradingSettings settings,
            TradingOperatingMode currentMode,
            BrokerReconciliationStatus status,
            InstrumentOnboardingStatus instrumentStatus = InstrumentOnboardingStatus.LiveEnabled)
        {
            var statusService = new FakeStatusService(status);
            return new TradingExecutionGuard(
                Options.Create(settings),
                new FakeModeService(currentMode),
                statusService,
                new FakeInstrumentRegistry(instrumentStatus));
        }

        private static BrokerReconciliationStatus ReadyStatus(BrokerEnvironmentVerificationStatus environment)
        {
            return new BrokerReconciliationStatus
            {
                State = TradingEngineState.Ready,
                TradingEnabled = true,
                ReconciliationCompleted = true,
                BrokerEnvironmentVerification = environment,
                ConnectedAccountId = "DU123"
            };
        }

        private static RiskDecision ApprovedRisk()
        {
            return new RiskDecision(
                RiskDecisionType.Approve,
                "ok",
                10m,
                1000m,
                50m,
                DateTime.UtcNow,
                context: PipelineContext.CreateForSignal("US-STK-SPY-SMART", "guard-test"));
        }

        private sealed class FakeInstrumentRegistry : IInstrumentRegistryService
        {
            private readonly InstrumentRegistrySnapshot _snapshot;

            public FakeInstrumentRegistry(InstrumentOnboardingStatus status)
            {
                _snapshot = new InstrumentRegistrySnapshot
                {
                    InstrumentId = "US-STK-SPY-SMART",
                    Symbol = "SPY",
                    ConfiguredEnabled = true,
                    TradingRequested = true,
                    Status = status
                };
            }

            public Task<InstrumentRegistrySnapshot?> GetAsync(string instrumentId, CancellationToken cancellationToken = default) =>
                Task.FromResult<InstrumentRegistrySnapshot?>(string.Equals(instrumentId, _snapshot.InstrumentId, StringComparison.OrdinalIgnoreCase) ? _snapshot : null);

            public Task<IReadOnlyList<InstrumentRegistrySnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<InstrumentRegistrySnapshot>>(new[] { _snapshot });

            public Task<InstrumentRegistrySyncResult> SynchronizeConfiguredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<InstrumentRegistrySnapshot> SetBrokerContractAsync(string instrumentId, int expectedVersion, long brokerContractId, string brokerPrimaryExchange, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public Task<InstrumentRegistrySnapshot> TransitionAsync(string instrumentId, InstrumentOnboardingStatus expectedStatus, InstrumentOnboardingStatus targetStatus, string reason, string trigger, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        private sealed class FakeModeService : IOperatingModeService
        {
            public FakeModeService(TradingOperatingMode currentMode)
            {
                CurrentMode = currentMode;
            }

            public TradingOperatingMode CurrentMode { get; private set; }

            public Task SetModeAsync(TradingOperatingMode mode, string reason, CancellationToken cancellationToken = default)
            {
                CurrentMode = mode;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeStatusService : ITradingEngineStatusService
        {
            public FakeStatusService(BrokerReconciliationStatus current)
            {
                Current = current;
            }

            public BrokerReconciliationStatus Current { get; private set; }

            public void SetState(
                TradingEngineState state,
                bool tradingEnabled,
                string message,
                IReadOnlyList<string>? mismatches = null,
                BrokerEnvironmentVerificationStatus brokerEnvironmentVerification = BrokerEnvironmentVerificationStatus.Unknown,
                string? connectedAccountId = null,
                bool reconciliationCompleted = false)
            {
                Current = new BrokerReconciliationStatus
                {
                    State = state,
                    TradingEnabled = tradingEnabled,
                    Message = message,
                    Mismatches = mismatches ?? Array.Empty<string>(),
                    BrokerEnvironmentVerification = brokerEnvironmentVerification,
                    ConnectedAccountId = connectedAccountId ?? string.Empty,
                    ReconciliationCompleted = reconciliationCompleted
                };
            }
        }
    }
}
