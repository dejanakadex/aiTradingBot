using TradingBot.Application.DTOs;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDataQualityService
    {
        Task<MarketDataQualityAssessment> EvaluateAsync(
            CanonicalMarketDataEvent marketEvent,
            bool allowStaleHistoricalData = false,
            CancellationToken cancellationToken = default);

        Task<IReadOnlyList<MarketDataStreamSnapshot>> GetStreamsAsync(CancellationToken cancellationToken = default);
    }

    public interface ILatestMarketDataService
    {
        void Apply(CanonicalMarketDataEvent marketEvent);
        LatestMarketDataSnapshot? Get(string instrumentIdOrSymbol);
        IReadOnlyList<LatestMarketDataSnapshot> GetAll();
    }
}
