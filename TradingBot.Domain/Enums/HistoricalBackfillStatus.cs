namespace TradingBot.Domain.Enums
{
    public enum HistoricalBackfillStatus
    {
        Pending = 0,
        Running = 10,
        RetryPending = 20,
        Completed = 30,
        CompletedWithGaps = 40,
        Faulted = 50
    }

    public enum HistoricalBackfillSegmentStatus
    {
        Running = 0,
        RetryPending = 10,
        Completed = 20,
        Faulted = 30
    }
}
