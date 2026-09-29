namespace TradingBot.Domain.Enums
{
    public enum SignalArbitrationStatus
    {
        Accepted = 0,
        Reserved = 1,
        Submitting = 2,
        Submitted = 3,
        PartiallyFilled = 4,
        Filled = 5,
        Closed = 6,
        Rejected = 7,
        Superseded = 8,
        Expired = 9,
        AnalysisOnly = 10
    }
}
