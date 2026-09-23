using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public static class InstrumentOnboardingTransitions
    {
        private static readonly IReadOnlyDictionary<InstrumentOnboardingStatus, IReadOnlySet<InstrumentOnboardingStatus>> Allowed =
            new Dictionary<InstrumentOnboardingStatus, IReadOnlySet<InstrumentOnboardingStatus>>
            {
                [InstrumentOnboardingStatus.Disabled] = Set(InstrumentOnboardingStatus.BackfillPending),
                [InstrumentOnboardingStatus.BackfillPending] = Set(
                    InstrumentOnboardingStatus.Backfilling,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.Backfilling] = Set(
                    InstrumentOnboardingStatus.Collecting,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.Collecting] = Set(
                    InstrumentOnboardingStatus.WarmingUp,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.WarmingUp] = Set(
                    InstrumentOnboardingStatus.ResearchReady,
                    InstrumentOnboardingStatus.Collecting,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.ResearchReady] = Set(
                    InstrumentOnboardingStatus.ShadowReady,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.ShadowReady] = Set(
                    InstrumentOnboardingStatus.PaperReady,
                    InstrumentOnboardingStatus.ResearchReady,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.PaperReady] = Set(
                    InstrumentOnboardingStatus.LiveEnabled,
                    InstrumentOnboardingStatus.ShadowReady,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.LiveEnabled] = Set(
                    InstrumentOnboardingStatus.Suspended,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.Suspended] = Set(
                    InstrumentOnboardingStatus.PaperReady,
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled,
                    InstrumentOnboardingStatus.Faulted),
                [InstrumentOnboardingStatus.Faulted] = Set(
                    InstrumentOnboardingStatus.BackfillPending,
                    InstrumentOnboardingStatus.Disabled)
            };

        public static bool IsAllowed(InstrumentOnboardingStatus from, InstrumentOnboardingStatus to)
        {
            return from == to || Allowed.TryGetValue(from, out var targets) && targets.Contains(to);
        }

        public static IReadOnlySet<InstrumentOnboardingStatus> GetAllowedTargets(InstrumentOnboardingStatus from)
        {
            return Allowed.TryGetValue(from, out var targets)
                ? targets
                : new HashSet<InstrumentOnboardingStatus>();
        }

        private static IReadOnlySet<InstrumentOnboardingStatus> Set(params InstrumentOnboardingStatus[] statuses)
        {
            return new HashSet<InstrumentOnboardingStatus>(statuses);
        }
    }
}
