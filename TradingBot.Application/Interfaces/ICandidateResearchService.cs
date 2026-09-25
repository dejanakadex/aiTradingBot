using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface ICandidateResearchService
    {
        string LabelVersion { get; }
        Task<int> PersistBatchAsync(PatternDetectionBatch batch, string sourceEventId, Guid? replayRunId = null, CancellationToken cancellationToken = default);
        Task MarkBlockedAsync(string candidateKey, string stage, IReadOnlyCollection<string> reasons, Guid? replayRunId = null, CancellationToken cancellationToken = default);
        Task<int> LabelMatureCandidatesAsync(DateTime asOfUtc, Guid? replayRunId = null, int? maximumCandidates = null, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ResearchCandidateSnapshot>> GetCandidatesAsync(string? instrumentId = null, ResearchCandidateOutcome? outcome = null, int count = 100, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<CandidateLabelSnapshot>> GetLabelsAsync(long researchCandidateId, CancellationToken cancellationToken = default);
    }
}
