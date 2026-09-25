using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class PatternEvaluation
    {
        public PatternType PatternType { get; init; }
        public TradeDirection Direction { get; init; }
        public string InstrumentId { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public string StrategyId { get; init; } = string.Empty;
        public Timeframe Timeframe { get; init; }
        public DateTime EvaluatedAtUtc { get; init; }
        public IReadOnlyList<PatternConditionResult> HardConditions { get; init; } = Array.Empty<PatternConditionResult>();
        public IReadOnlyList<PatternScoreComponent> ScoreComponents { get; init; } = Array.Empty<PatternScoreComponent>();
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
        public PatternCandidate? Candidate { get; init; }
        public bool Accepted => Candidate != null;
        public decimal FinalScore => ScoreComponents.Sum(component => component.WeightedScore);

        public static PatternEvaluation FromCandidate(PatternCandidate candidate) => new()
        {
            PatternType = candidate.PatternType,
            Direction = candidate.Direction,
            InstrumentId = candidate.InstrumentId,
            Symbol = candidate.Symbol,
            StrategyId = candidate.StrategyId,
            Timeframe = candidate.Timeframe,
            EvaluatedAtUtc = candidate.DetectedAtUtc,
            HardConditions = candidate.HardConditions,
            ScoreComponents = candidate.ScoreComponents,
            Reasons = candidate.EvaluationReasons,
            Candidate = candidate
        };
    }
}
