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
        Cancelled,
        Rejected
    }
}
