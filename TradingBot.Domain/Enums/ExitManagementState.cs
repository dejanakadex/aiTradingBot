namespace TradingBot.Domain.Enums
{
    public enum ExitManagementState
    {
        WaitingForEntryFill,
        InitialProtection,
        BreakEvenProtection,
        Trailing,
        Closed,
        Faulted
    }
}
