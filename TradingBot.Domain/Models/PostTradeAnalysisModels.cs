using System;
using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class PostTradeExecutionData
    {
        public string ExecutionId { get; init; } = string.Empty;
        public string OrderId { get; init; } = string.Empty;
        public DateTime ExecutedAtUtc { get; init; }
        public decimal Quantity { get; init; }
        public decimal Price { get; init; }
        public decimal? Commission { get; init; }
        public string Status { get; init; } = string.Empty;
    }

    public sealed class PostTradeAnalysisInput
    {
        public int? SourceTradeId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public DateTime OpenedUtc { get; init; }
        public DateTime ClosedUtc { get; init; }
        public decimal Quantity { get; init; }
        public decimal EntryPrice { get; init; }
        public decimal ExitPrice { get; init; }
        public decimal CommissionsFees { get; init; }
        public MarketSnapshot MarketSnapshot { get; init; } = new();
        public PatternCandidate? PatternCandidate { get; init; }
        public AiMarketAnalysisResult AiMarketAnalysis { get; init; } = new();
        public AiTradeCriticResult AiCriticAnalysis { get; init; } = new();
        public StrategyDecision StrategyDecision { get; init; } = new();
        public RiskDecision? RiskDecision { get; init; }
        public IReadOnlyList<ManagedOrderResult> Orders { get; init; } = Array.Empty<ManagedOrderResult>();
        public IReadOnlyList<PostTradeExecutionData> Executions { get; init; } = Array.Empty<PostTradeExecutionData>();
        public IReadOnlyList<Candle> CandlesDuringTrade { get; init; } = Array.Empty<Candle>();
        public string TradeResult { get; init; } = string.Empty;
    }

    public sealed class PostTradeAnalysisRecordSummary
    {
        public int Id { get; init; }
        public int? SourceTradeId { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string Pattern { get; init; } = string.Empty;
        public DateTime OpenedUtc { get; init; }
        public DateTime ClosedUtc { get; init; }
        public decimal GrossProfitLoss { get; init; }
        public decimal CommissionsFees { get; init; }
        public decimal NetProfitLoss { get; init; }
        public decimal? MaximumFavorableExcursion { get; init; }
        public decimal? MaximumAdverseExcursion { get; init; }
        public decimal HoldingDurationMinutes { get; init; }
    }

    public sealed class LosingConditionSummary
    {
        public string ConditionType { get; init; } = string.Empty;
        public string ConditionValue { get; init; } = string.Empty;
        public int LosingTrades { get; init; }
    }

    public sealed class StrategyImprovementRecommendation
    {
        public string Recommendation { get; init; } = string.Empty;
        public string Rationale { get; init; } = string.Empty;
        public bool RequiresManualApproval { get; init; } = true;
    }
}
