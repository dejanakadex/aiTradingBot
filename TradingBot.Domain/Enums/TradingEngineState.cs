namespace TradingBot.Domain.Enums
{
    public enum TradingEngineState
    {
        Starting,
        Connecting,
        Reconciling,
        Ready,
        Paused,
        Degraded,
        Faulted
    }
}
