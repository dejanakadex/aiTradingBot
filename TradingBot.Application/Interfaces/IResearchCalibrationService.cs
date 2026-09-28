using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IResearchCalibrationService
    {
        string CalibrationVersion { get; }
        Task<ResearchCalibrationProfileSnapshot> GenerateAsync(ResearchCalibrationRequest request, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ResearchCalibrationProfileSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default);
        Task<ResearchCalibrationProfileSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<ResearchCalibrationProfileSnapshot> DecideAsync(Guid id, ResearchCalibrationDecisionRequest request, CancellationToken cancellationToken = default);
        Task<OpportunityRankingResult> RankAsync(Guid id, OpportunityRankingRequest request, CancellationToken cancellationToken = default);
    }
}
