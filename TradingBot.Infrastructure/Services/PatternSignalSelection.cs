using TradingBot.Application.Configuration;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services;

/// <summary>Shared deterministic ranking for stock and FX profiles.</summary>
internal static class PatternSignalSelection
{
    public static PatternCandidate? Select(IEnumerable<PatternCandidate> candidates, ShortSignalProfile profile) => candidates
        .Where(candidate => profile.PatternTypes.Contains(candidate.PatternType) && candidate.Confidence >= profile.MinimumConfidence)
        .OrderByDescending(candidate => candidate.Confidence).ThenBy(candidate => candidate.PatternType).FirstOrDefault();
}
