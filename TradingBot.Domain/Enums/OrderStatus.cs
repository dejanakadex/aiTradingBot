namespace TradingBot.Domain.Enums
{
    public enum OrderStatus
    {
        New,
        PendingBrokerConfirmation,
        Unknown,
        Submitted,
        PartiallyFilled,
        Filled,
        CancelPending,
        Cancelled,
        Rejected
    }
}
