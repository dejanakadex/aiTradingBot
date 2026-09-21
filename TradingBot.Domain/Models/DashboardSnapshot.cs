using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class DashboardSnapshot
    {
        public string ApplicationState { get; init; } = "Running";
        public string OperatingMode { get; init; } = "AnalysisOnly";
        public string IbkrConnectionState { get; init; } = "Unavailable";
        public string IbkrEnvironmentVerification { get; init; } = "Unknown";
        public string ConnectedAccountId { get; init; } = string.Empty;
        public string ReconciliationState { get; init; } = "Starting";
        public string ReconciliationMessage { get; init; } = string.Empty;
        public bool KillSwitchActive { get; init; }
        public bool LiveTradingExplicitlyEnabled { get; init; }
        public TradingPipelineActivitySnapshot PipelineActivity { get; init; } = new();
        public TradingEngineState TradingEngineState { get; init; } = TradingEngineState.Starting;
        public bool TradingEnabled { get; init; }
        public string CurrentSymbol { get; init; } = string.Empty;
        public decimal? CurrentPrice { get; init; }
        public decimal CurrentPosition { get; init; }
        public string MarketDataStatus { get; init; } = "No candles yet";
        public string ConfiguredMarketData { get; init; } = string.Empty;
        public DateTime? LatestCandleUtc { get; init; }
        public int CandlesToday { get; init; }
        public string FifteenMinuteTrend { get; init; } = "Unknown";
        public string FiveMinuteMarketContext { get; init; } = "Unknown";
        public string OneMinuteDetectedPattern { get; init; } = "None";
        public string LatestAiAnalysis { get; init; } = "None";
        public decimal? AiConfidence { get; init; }
        public string AnalyzerModel { get; init; } = string.Empty;
        public string AnalyzerPrompt { get; init; } = string.Empty;
        public string LatestAnalyzerDecision { get; init; } = "None";
        public decimal? AnalyzerConfidence { get; init; }
        public string CriticModel { get; init; } = string.Empty;
        public string CriticPrompt { get; init; } = string.Empty;
        public string LatestCriticDecision { get; init; } = "None";
        public int AiCallsToday { get; init; }
        public int AnalyzerCallsToday { get; init; }
        public int CriticCallsToday { get; init; }
        public int PatternsRejectedBeforeAiToday { get; init; }
        public int PatternsSentToAiToday { get; init; }
        public IReadOnlyList<PatternGateRejectionSnapshot> RecentPatternGateRejections { get; init; } = Array.Empty<PatternGateRejectionSnapshot>();
        public int CriticSkippedToday { get; init; }
        public int AnalyzerBuyToday { get; init; }
        public int AnalyzerWaitToday { get; init; }
        public int AnalyzerRejectToday { get; init; }
        public string LatestPipelineDecision { get; init; } = "None";
        public string LatestPipelineDecisionReason { get; init; } = string.Empty;
        public DateTime? LatestPipelineDecisionUtc { get; init; }
        public DateTime? LatestAnalyzerDecisionUtc { get; init; }
        public DateTime? LatestCriticDecisionUtc { get; init; }
        public string LatestStrategyDecision { get; init; } = "None";
        public DateTime? LatestStrategyDecisionUtc { get; init; }
        public string LatestRiskDecision { get; init; } = "None";
        public DateTime? LatestRiskDecisionUtc { get; init; }
        public string LatestOrderStatus { get; init; } = "None";
        public DateTime? LatestOrderStatusUtc { get; init; }
        public int AnalyzerInputTokensToday { get; init; }
        public int AnalyzerOutputTokensToday { get; init; }
        public int CriticInputTokensToday { get; init; }
        public int CriticOutputTokensToday { get; init; }
        public int InputTokensToday { get; init; }
        public int OutputTokensToday { get; init; }
        public int TotalTokensToday { get; init; }
        public decimal AnalyzerEstimatedCostToday { get; init; }
        public decimal CriticEstimatedCostToday { get; init; }
        public decimal AiEstimatedCostToday { get; init; }
        public int TodaysTrades { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public decimal GrossProfitLoss { get; init; }
        public decimal Fees { get; init; }
        public decimal NetProfitLoss { get; init; }
        public decimal CurrentExposure { get; init; }
        public decimal? CurrentLeverage { get; init; }
        public decimal DailyLoss { get; init; }
        public decimal DailyLossLimit { get; init; }
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
