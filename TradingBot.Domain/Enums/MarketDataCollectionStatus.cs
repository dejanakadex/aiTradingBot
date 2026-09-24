namespace TradingBot.Domain.Enums
{
    public enum MarketDataCollectionStatus
    {
        WaitingForConnection = 1,
        Subscribing = 2,
        Live = 3,
        IdleOutsideSession = 4,
        Stale = 5,
        Reconnecting = 6,
        Faulted = 7,
        Stopped = 8
    }
}
