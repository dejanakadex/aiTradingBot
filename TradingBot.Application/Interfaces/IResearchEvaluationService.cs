using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IResearchEvaluationService
    {
        string EvaluationVersion { get; }
        Task<ResearchEvaluationRunSnapshot> RunAsync(ResearchEvaluationRequest request, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ResearchEvaluationRunSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default);
        Task<ResearchEvaluationRunSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    }
}
