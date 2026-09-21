namespace TradingBot.Domain.Enums
{
    public enum TradingPipelineStage
    {
        WaitingForCandle = 0,
        ReceivingCandle = 1,
        FetchingMarketContext = 2,
        PatternAnalysis = 3,
        AiAnalyzer = 4,
        AiCritic = 5,
        StrategyAndRisk = 6,
        OrderExecution = 7
    }
}
