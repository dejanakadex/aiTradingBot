using System.Collections.Generic;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPatternDetector
    {
        /// <summary>
        /// Analyze the provided candles (ascending by time) and return any detected PatternCandidates.
        /// Implementations should be deterministic and not call external services.
        /// </summary>
        IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<Candle> candles);

        IReadOnlyList<PatternCandidate> Detect(PatternDetectionInput input) => Detect(input.Candles);

        IReadOnlyList<PatternEvaluation> Evaluate(PatternDetectionInput input) =>
            Detect(input).Select(PatternEvaluation.FromCandidate).ToArray();
    }
}
