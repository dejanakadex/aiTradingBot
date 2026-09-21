using System;

namespace TradingBot.Persistence
{
    public class PostTradeAnalysisRecord
    {
        public int Id { get; set; }
        public int? SourceTradeId { get; set; }
        public string Symbol { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public DateTime OpenedUtc { get; set; }
        public DateTime ClosedUtc { get; set; }
        public decimal Quantity { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal GrossProfitLoss { get; set; }
        public decimal CommissionsFees { get; set; }
        public decimal NetProfitLoss { get; set; }
        public decimal? MaximumFavorableExcursion { get; set; }
        public decimal? MaximumAdverseExcursion { get; set; }
        public decimal HoldingDurationMinutes { get; set; }
        public string MarketRegime { get; set; } = string.Empty;
        public bool? AboveVwap { get; set; }
        public string FifteenMinuteTrend { get; set; } = string.Empty;
        public string AiDecision { get; set; } = string.Empty;
        public decimal? AiConfidence { get; set; }
        public string CriticDecision { get; set; } = string.Empty;
        public string StrategyDecision { get; set; } = string.Empty;
        public string RiskDecision { get; set; } = string.Empty;
        public string TradeResult { get; set; } = string.Empty;
        public string MarketSnapshotJson { get; set; } = string.Empty;
        public string PatternCandidateJson { get; set; } = string.Empty;
        public string AiMarketAnalysisJson { get; set; } = string.Empty;
        public string AiCriticAnalysisJson { get; set; } = string.Empty;
        public string StrategyDecisionJson { get; set; } = string.Empty;
        public string RiskDecisionJson { get; set; } = string.Empty;
        public string OrderDataJson { get; set; } = string.Empty;
        public string ExecutionDataJson { get; set; } = string.Empty;
        public string TradeResultJson { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
    }
}
