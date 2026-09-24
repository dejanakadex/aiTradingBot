namespace TradingBot.Domain.Enums
{
    public enum ReplayRunStatus
    {
        Pending = 0,
        Running = 1,
        Paused = 2,
        Completed = 3,
        Cancelled = 4,
        Faulted = 5
    }
}
