using System;
using System.Collections.Generic;

namespace TradingBot.Domain.Models
{
    public sealed class TradeDashboardRow
    {
        public DateTime TimestampUtc { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string Pattern { get; init; } = string.Empty;
        public decimal? AiConfidence { get; init; }
        public decimal Entry { get; init; }
        public decimal? Exit { get; init; }
        public decimal Quantity { get; init; }
        public decimal GrossProfitLoss { get; init; }
        public decimal Fees { get; init; }
        public decimal NetProfitLoss { get; init; }
        public string TradeResult { get; init; } = string.Empty;
    }

    public sealed class PatternAnalysisFilter
    {
        public string? Pattern { get; init; }
        public string? Symbol { get; init; }
        public DateTime? FromUtc { get; init; }
        public DateTime? ToUtc { get; init; }
        public string? MarketRegime { get; init; }
        public bool? AboveVwap { get; init; }
        public string? FifteenMinuteTrend { get; init; }
        public string? AiDecision { get; init; }
    }

    public sealed class PatternStatisticsRow
    {
        public string Pattern { get; init; } = string.Empty;
        public int Detections { get; init; }
        public int Trades { get; init; }
        public int Wins { get; init; }
        public int Losses { get; init; }
        public decimal WinRate { get; init; }
        public decimal AverageGrossReturn { get; init; }
        public decimal AverageNetReturn { get; init; }
        public decimal? ProfitFactor { get; init; }
    }

    public sealed class PatternAnalysisRow
    {
        public DateTime TimestampUtc { get; init; }
        public string Symbol { get; init; } = string.Empty;
        public string PatternCandidate { get; init; } = string.Empty;
        public string AiDecision { get; init; } = string.Empty;
        public decimal? AiConfidence { get; init; }
        public string CriticDecision { get; init; } = string.Empty;
        public string StrategyDecision { get; init; } = string.Empty;
        public string RiskDecision { get; init; } = string.Empty;
        public string ActualTradeOutcome { get; init; } = string.Empty;
        public string MarketRegime { get; init; } = string.Empty;
        public bool? AboveVwap { get; init; }
        public string FifteenMinuteTrend { get; init; } = string.Empty;
    }

    public sealed class PatternAnalysisDashboard
    {
        public IReadOnlyList<PatternStatisticsRow> Statistics { get; init; } = Array.Empty<PatternStatisticsRow>();
        public IReadOnlyList<PatternAnalysisRow> Rows { get; init; } = Array.Empty<PatternAnalysisRow>();
    }
}
