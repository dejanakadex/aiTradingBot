using System.Collections.Concurrent;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class MarketDataCollectionStatusService : IMarketDataCollectionStatusService
    {
        private readonly ConcurrentDictionary<string, MarketDataCollectionStreamSnapshot> _streams = new(StringComparer.OrdinalIgnoreCase);

        public void EnsureStream(
            string streamKey,
            string instrumentId,
            string symbol,
            string timeframe,
            int heartbeatTimeoutSeconds,
            DateTime nowUtc)
        {
            _streams.TryAdd(streamKey, new MarketDataCollectionStreamSnapshot
            {
                StreamKey = streamKey,
                InstrumentId = instrumentId,
                Symbol = symbol,
                Timeframe = timeframe,
                Status = MarketDataCollectionStatus.WaitingForConnection,
                StatusReason = "Collection stream initialized.",
                HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds,
                UpdatedAtUtc = ToUtc(nowUtc)
            });
        }

        public void SeedCheckpoint(string streamKey, DateTime eventTimeUtc, DateTime heartbeatUtc, long receiveLagMilliseconds)
        {
            Update(streamKey, current => current with
            {
                LastHeartbeatUtc = ToUtc(heartbeatUtc),
                LastEventTimeUtc = ToUtc(eventTimeUtc),
                ReceiveLagMilliseconds = Math.Max(0, receiveLagMilliseconds),
                StatusReason = "Collection checkpoint restored from the latest operational candle.",
                UpdatedAtUtc = ToUtc(heartbeatUtc)
            });
        }

        public void ReportStatus(
            string streamKey,
            MarketDataCollectionStatus status,
            string reason,
            bool brokerConnected,
            bool subscribed,
            DateTime nowUtc,
            string? error = null)
        {
            Update(streamKey, current => current with
            {
                Status = status,
                StatusReason = reason,
                BrokerConnected = brokerConnected,
                Subscribed = subscribed,
                ConsecutiveFailures = status is MarketDataCollectionStatus.Stale or MarketDataCollectionStatus.Reconnecting or MarketDataCollectionStatus.Faulted
                    ? current.ConsecutiveFailures + 1
                    : current.ConsecutiveFailures,
                LastError = error ?? current.LastError,
                UpdatedAtUtc = ToUtc(nowUtc)
            });
        }

        public void ReportHeartbeat(string streamKey, DateTime eventTimeUtc, DateTime heartbeatUtc, long receiveLagMilliseconds)
        {
            Update(streamKey, current => current with
            {
                Status = MarketDataCollectionStatus.Live,
                StatusReason = "Market-data heartbeat received.",
                BrokerConnected = true,
                Subscribed = true,
                LastHeartbeatUtc = ToUtc(heartbeatUtc),
                LastEventTimeUtc = ToUtc(eventTimeUtc),
                ReceiveLagMilliseconds = Math.Max(0, receiveLagMilliseconds),
                ConsecutiveFailures = 0,
                LastError = string.Empty,
                UpdatedAtUtc = ToUtc(heartbeatUtc)
            });
        }

        public void ReportReconnect(string streamKey, string reason, DateTime nowUtc)
        {
            Update(streamKey, current => current with
            {
                Status = MarketDataCollectionStatus.Reconnecting,
                StatusReason = reason,
                BrokerConnected = true,
                Subscribed = false,
                ReconnectCount = current.ReconnectCount + 1,
                ConsecutiveFailures = current.ConsecutiveFailures + 1,
                LastError = reason,
                UpdatedAtUtc = ToUtc(nowUtc)
            });
        }

        public void ReportGapFill(string streamKey, DateTime startUtc, DateTime endUtc, int barsInserted, DateTime nowUtc)
        {
            Update(streamKey, current => current with
            {
                GapFillCount = current.GapFillCount + 1,
                LastGapFillStartUtc = ToUtc(startUtc),
                LastGapFillEndUtc = ToUtc(endUtc),
                LastGapFillBars = Math.Max(0, barsInserted),
                UpdatedAtUtc = ToUtc(nowUtc)
            });
        }

        public IReadOnlyList<MarketDataCollectionStreamSnapshot> GetAll() => _streams.Values
            .OrderBy(item => item.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Timeframe, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private void Update(string streamKey, Func<MarketDataCollectionStreamSnapshot, MarketDataCollectionStreamSnapshot> update)
        {
            _streams.AddOrUpdate(
                streamKey,
                _ => throw new InvalidOperationException($"Collection stream '{streamKey}' was not initialized."),
                (_, current) => update(current));
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
