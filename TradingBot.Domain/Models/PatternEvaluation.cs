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
        public string FeatureVersion { get; init; } = string.Empty;
        public string PatternVersion { get; init; } = string.Empty;
        public Timeframe Timeframe { get; init; }
        public DateTime EvaluatedAtUtc { get; init; }
        public decimal ReferencePrice { get; init; }
        public IReadOnlyList<PatternConditionResult> HardConditions { get; init; } = Array.Empty<PatternConditionResult>();
        public IReadOnlyList<PatternScoreComponent> ScoreComponents { get; init; } = Array.Empty<PatternScoreComponent>();
        public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
        public PatternCandidate? Candidate { get; init; }
        public bool Accepted => Candidate != null;
        public decimal FinalScore => ScoreComponents.Sum(component => component.WeightedScore);
        public string EvaluationKey => $"{InstrumentId}|{StrategyId}|{Direction}|{PatternType}|{Timeframe}|{EvaluatedAtUtc:O}".ToUpperInvariant();

        public static PatternEvaluation FromCandidate(PatternCandidate candidate) => new()
        {
            PatternType = candidate.PatternType,
            Direction = candidate.Direction,
            InstrumentId = candidate.InstrumentId,
            Symbol = candidate.Symbol,
            StrategyId = candidate.StrategyId,
            FeatureVersion = candidate.Context.FeatureVersion,
            PatternVersion = candidate.PatternVersion,
            Timeframe = candidate.Timeframe,
            EvaluatedAtUtc = candidate.DetectedAtUtc,
            ReferencePrice = candidate.Metadata.TryGetValue("referencePrice", out var price)
                && decimal.TryParse(price, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : candidate.RelevantPriceLevels.LastOrDefault(),
            HardConditions = candidate.HardConditions,
            ScoreComponents = candidate.ScoreComponents,
            Reasons = candidate.EvaluationReasons,
            Candidate = candidate
        };
    }
}
