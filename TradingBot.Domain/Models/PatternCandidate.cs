using System;
using System.Collections.Generic;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class PatternCandidate
    {
        public PatternType PatternType { get; }
        public string Symbol { get; }
        public Timeframe Timeframe { get; }
        public DateTime DetectedAtUtc { get; }
        public decimal Confidence { get; }
        public IReadOnlyList<decimal> RelevantPriceLevels { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }
        public PipelineContext Context { get; }
        public TradeDirection Direction { get; }
        public IReadOnlyList<PatternConditionResult> HardConditions { get; }
        public IReadOnlyList<PatternScoreComponent> ScoreComponents { get; }
        public IReadOnlyList<string> EvaluationReasons { get; }
        public string InstrumentId => Context.InstrumentId;
        public string StrategyId => Context.StrategyId;
        public string PatternVersion => Context.PatternVersion;
        public string PatternKey => $"{InstrumentId}|{StrategyId}|{Direction}|{PatternType}|{Timeframe}|{DetectedAtUtc:O}".ToUpperInvariant();

        public PatternCandidate(PatternType patternType, string symbol, Timeframe timeframe, DateTime detectedAtUtc, decimal confidence,
            IEnumerable<decimal>? relevantPriceLevels = null, IDictionary<string, string>? metadata = null, PipelineContext? context = null,
            TradeDirection direction = TradeDirection.Long,
            IEnumerable<PatternConditionResult>? hardConditions = null,
            IEnumerable<PatternScoreComponent>? scoreComponents = null,
            IEnumerable<string>? evaluationReasons = null)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (confidence < 0m || confidence > 1m) throw new ArgumentOutOfRangeException(nameof(confidence), "confidence must be between 0 and 1");
            if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction), "direction must be supported");

            PatternType = patternType;
            Symbol = symbol;
            Timeframe = timeframe;
            DetectedAtUtc = detectedAtUtc.Kind switch
            {
                DateTimeKind.Utc => detectedAtUtc,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(detectedAtUtc, DateTimeKind.Utc),
                _ => detectedAtUtc.ToUniversalTime()
            };
            Confidence = confidence;
            RelevantPriceLevels = new List<decimal>(relevantPriceLevels ?? Array.Empty<decimal>());
            Metadata = new Dictionary<string, string>(metadata ?? new Dictionary<string, string>());
            Direction = direction;
            HardConditions = new List<PatternConditionResult>(hardConditions ?? Array.Empty<PatternConditionResult>());
            ScoreComponents = new List<PatternScoreComponent>(scoreComponents ?? Array.Empty<PatternScoreComponent>());
            EvaluationReasons = new List<string>(evaluationReasons ?? Array.Empty<string>());
            Context = context ?? PipelineContext.CreateForSignal(
                Symbol.Trim().ToUpperInvariant(),
                $"{Direction}|{PatternType}|{Timeframe}|{DetectedAtUtc:O}");
        }
    }
}
