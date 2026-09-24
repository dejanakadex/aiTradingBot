using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.Interfaces
{
    public interface IMarketDataCollectionStatusService
    {
        void EnsureStream(string streamKey, string instrumentId, string symbol, string timeframe, int heartbeatTimeoutSeconds, DateTime nowUtc);
        void SeedCheckpoint(string streamKey, DateTime eventTimeUtc, DateTime heartbeatUtc, long receiveLagMilliseconds);
        void ReportStatus(string streamKey, MarketDataCollectionStatus status, string reason, bool brokerConnected, bool subscribed, DateTime nowUtc, string? error = null);
        void ReportHeartbeat(string streamKey, DateTime eventTimeUtc, DateTime heartbeatUtc, long receiveLagMilliseconds);
        void ReportReconnect(string streamKey, string reason, DateTime nowUtc);
        void ReportGapFill(string streamKey, DateTime startUtc, DateTime endUtc, int barsInserted, DateTime nowUtc);
        IReadOnlyList<MarketDataCollectionStreamSnapshot> GetAll();
    }
}
