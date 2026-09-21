namespace TradingBot.Domain.Enums
{
    public enum ExitStopUpdateReason
    {
        InitialProtection,
        PartialFillProtectionAdjusted,
        BreakEvenActivated,
        TrailingActivated,
        TrailingAdvanced,
        Reconciliation,
        EmergencyProtection,
        MaximumHoldingTimeExit,
        RejectedStop
    }
}
