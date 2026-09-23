namespace TradingBot.Domain.Enums
{
    public enum InstrumentOnboardingStatus
    {
        Disabled = 0,
        BackfillPending = 10,
        Backfilling = 20,
        Collecting = 30,
        WarmingUp = 40,
        ResearchReady = 50,
        ShadowReady = 60,
        PaperReady = 70,
        LiveEnabled = 80,
        Suspended = 90,
        Faulted = 100
    }
}
