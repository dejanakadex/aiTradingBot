using System;

namespace TradingBot.Domain.Models
{
    public sealed class ApprovedTradePlan
    {
        public MarketSnapshot Snapshot { get; init; } = new();
        public PatternCandidate Pattern { get; init; } = null!;
        public AiMarketAnalysisResult AiAnalysis { get; init; } = new();
        public AiTradeCriticResult AiCriticAnalysis { get; init; } = new();
        public StrategyDecision StrategyDecision { get; init; } = new();
        public RiskDecision RiskDecision { get; init; } = null!;
        public DateTime ApprovedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
