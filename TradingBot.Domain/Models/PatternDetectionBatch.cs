namespace TradingBot.Domain.Models
{
    public sealed class PatternDetectionBatch
    {
        public IReadOnlyList<PatternEvaluation> Evaluations { get; init; } = Array.Empty<PatternEvaluation>();
        public IReadOnlyList<PatternCandidate> Candidates { get; init; } = Array.Empty<PatternCandidate>();
    }
}
