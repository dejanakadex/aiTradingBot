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
        Task<IReadOnlyList<MarketDataQualityIncidentSnapshot>> GetRecentIncidentsAsync(
            int count = 100,
            CancellationToken cancellationToken = default);
    }

    public interface ILatestMarketDataService
    {
        void Apply(CanonicalMarketDataEvent marketEvent);
        void AdvanceTo(DateTime receivedTimeUtc);
        IReadOnlyList<ShortIntervalBarSnapshot> GetCompletedShortAggregates(string instrumentId, int intervalSeconds, DateTime asOfUtc, int count = 100);
        LatestMarketDataSnapshot? Get(string instrumentIdOrSymbol);
        IReadOnlyList<LatestMarketDataSnapshot> GetAll();
    }
}
