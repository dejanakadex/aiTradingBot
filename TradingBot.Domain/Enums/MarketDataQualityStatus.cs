namespace TradingBot.Domain.Enums
{
    public enum MarketDataQualityStatus
    {
        Healthy = 0,
        NonFinal = 10,
        Duplicate = 20,
        Stale = 30,
        Future = 40,
        OutOfOrder = 50,
        GapDetected = 60,
        Invalid = 70
    }
}
