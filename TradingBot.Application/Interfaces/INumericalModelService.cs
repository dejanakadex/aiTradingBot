using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface INumericalModelService
    {
        Task<NumericalModelSnapshot> TrainAsync(NumericalModelTrainingRequest request, CancellationToken cancellationToken = default);
        Task<NumericalModelSnapshot> DecideAsync(Guid id, NumericalModelDecisionRequest request, CancellationToken cancellationToken = default);
        Task<NumericalModelSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<NumericalModelSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default);
        Task<NumericalModelPrediction> PredictAsync(NumericalModelPredictionRequest request, CancellationToken cancellationToken = default);
    }
}
