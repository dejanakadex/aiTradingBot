namespace TradingBot.Domain.Enums
{
    public enum ExitManagementState
    {
        WaitingForEntryFill,
        InitialProtection,
        BreakEvenProtection,
        Trailing,
        ExitCancelPending,
        ExitSubmitted,
        Closed,
        Faulted
    }
}
