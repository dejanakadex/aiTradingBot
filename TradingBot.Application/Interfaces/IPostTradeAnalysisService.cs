using TradingBot.Domain.Models;

namespace TradingBot.Application.Interfaces
{
    public interface IPostTradeAnalysisService
    {
        Task<PostTradeAnalysisRecordSummary> StoreCompletedTradeAnalysisAsync(PostTradeAnalysisInput input, CancellationToken cancellationToken = default);

        Task<PatternStatisticsRow> GetPatternPerformanceAsync(string pattern, PatternAnalysisFilter? filter = null, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<LosingConditionSummary>> GetCommonLosingConditionsAsync(PatternAnalysisFilter? filter = null, int take = 10, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<StrategyImprovementRecommendation>> GetStrategyImprovementRecommendationsAsync(PatternAnalysisFilter? filter = null, CancellationToken cancellationToken = default);
    }
}
