using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class MarketDataQualityService : IMarketDataQualityService
    {
        private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(5);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly TradingSettings _settings;
        private readonly IClock _clock;
        private readonly ILogger<MarketDataQualityService> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _streamLocks = new(StringComparer.OrdinalIgnoreCase);

        public MarketDataQualityService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IOptions<TradingSettings> settings,
            IClock clock,
            ILogger<MarketDataQualityService> logger)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MarketDataQualityAssessment> EvaluateAsync(
            CanonicalMarketDataEvent marketEvent,
            bool allowStaleHistoricalData = false,
            CancellationToken cancellationToken = default)
        {
            if (marketEvent == null) throw new ArgumentNullException(nameof(marketEvent));

            var streamKey = marketEvent.StreamKey;
            var gate = _streamLocks.GetOrAdd(streamKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var state = await db.MarketDataStreamStateRecords
                    .SingleOrDefaultAsync(item => item.StreamKey == streamKey, cancellationToken)
                    .ConfigureAwait(false);

                var assessment = Assess(marketEvent, state, allowStaleHistoricalData);
                var now = ToUtc(_clock.UtcNow);
                state ??= CreateState(marketEvent, streamKey, now);
                if (state.Id == 0)
                {
                    db.MarketDataStreamStateRecords.Add(state);
                }

                ApplyAssessment(state, marketEvent, assessment, now);
                if (!assessment.IsHealthy)
                {
                    db.MarketDataQualityIncidentRecords.Add(new MarketDataQualityIncidentRecord
                    {
                        EventId = marketEvent.EventId,
                        StreamKey = streamKey,
                        InstrumentId = marketEvent.InstrumentId,
                        Symbol = marketEvent.Symbol,
                        Kind = marketEvent.Kind,
                        Timeframe = marketEvent.Timeframe,
                        Source = marketEvent.Source,
                        EventTimeUtc = ToUtc(marketEvent.EventTimeUtc),
                        ReceivedTimeUtc = ToUtc(marketEvent.ReceivedTimeUtc),
                        Sequence = marketEvent.Sequence,
                        Status = assessment.Status,
                        Reason = assessment.Reason,
                        RecordedAtUtc = now
                    });
                }

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (!assessment.IsHealthy)
                {
                    _logger.LogWarning(
                        "Market data quality {Status} for {InstrumentId} {Kind} {Timeframe}: {Reason}",
                        assessment.Status,
                        marketEvent.InstrumentId,
                        marketEvent.Kind,
                        marketEvent.Timeframe,
                        assessment.Reason);
                }

                return assessment;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<IReadOnlyList<MarketDataStreamSnapshot>> GetStreamsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.MarketDataStreamStateRecords
                .AsNoTracking()
                .OrderBy(item => item.Symbol)
                .ThenBy(item => item.Kind)
                .ThenBy(item => item.Timeframe)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<IReadOnlyList<MarketDataQualityIncidentSnapshot>> GetRecentIncidentsAsync(
            int count = 100,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.MarketDataQualityIncidentRecords
                .AsNoTracking()
                .OrderByDescending(item => item.RecordedAtUtc)
                .ThenByDescending(item => item.Id)
                .Take(Math.Clamp(count, 1, 500))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            return records.Select(record => new MarketDataQualityIncidentSnapshot
            {
                Id = record.Id,
                EventId = record.EventId,
                StreamKey = record.StreamKey,
                InstrumentId = record.InstrumentId,
                Symbol = record.Symbol,
                Kind = record.Kind,
                Timeframe = record.Timeframe,
                Source = record.Source,
                EventTimeUtc = ToUtc(record.EventTimeUtc),
                ReceivedTimeUtc = ToUtc(record.ReceivedTimeUtc),
                Sequence = record.Sequence,
                Status = record.Status,
                Reason = record.Reason,
                RecordedAtUtc = ToUtc(record.RecordedAtUtc)
            }).ToArray();
        }

        private MarketDataQualityAssessment Assess(
            CanonicalMarketDataEvent marketEvent,
            MarketDataStreamStateRecord? state,
            bool allowStaleHistoricalData)
        {
            var invalidReason = ValidateShape(marketEvent);
            if (invalidReason != null)
            {
                return Reject(MarketDataQualityStatus.Invalid, invalidReason);
            }

            var configured = _settings.GetConfiguredInstruments()
                .SingleOrDefault(item => item.InstrumentId.Equals(marketEvent.InstrumentId, StringComparison.OrdinalIgnoreCase));
            if (configured == null)
            {
                return Reject(MarketDataQualityStatus.Invalid, $"Instrument '{marketEvent.InstrumentId}' is not configured.");
            }
            if (!configured.Enabled)
            {
                return Reject(MarketDataQualityStatus.Invalid, $"Instrument '{marketEvent.InstrumentId}' is disabled.");
            }
            if (!configured.Symbol.Equals(marketEvent.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(MarketDataQualityStatus.Invalid, $"Symbol '{marketEvent.Symbol}' does not match configured instrument '{marketEvent.InstrumentId}'.");
            }

            if (!marketEvent.IsFinal)
            {
                return Reject(MarketDataQualityStatus.NonFinal, "Event is not final.");
            }

            var eventTime = ToUtc(marketEvent.EventTimeUtc);
            var receivedTime = ToUtc(marketEvent.ReceivedTimeUtc);
            if (eventTime > receivedTime.Add(FutureTolerance))
            {
                return Reject(MarketDataQualityStatus.Future, "Event time is later than receive time tolerance.");
            }

            var maximumAgeSeconds = marketEvent.Kind == MarketDataEventKind.Bar
                ? _settings.MaximumCandleAgeSeconds + (int)(ExpectedBarInterval(marketEvent.Timeframe)?.TotalSeconds ?? 0d)
                : _settings.MaximumQuoteAgeSeconds;
            if (!allowStaleHistoricalData && receivedTime - eventTime > TimeSpan.FromSeconds(Math.Max(1, maximumAgeSeconds)))
            {
                return Reject(MarketDataQualityStatus.Stale, "Event is stale at receipt.");
            }

            if (state?.LastEventId == marketEvent.EventId
                || marketEvent.Kind == MarketDataEventKind.Bar && state?.LastEventTimeUtc == eventTime)
            {
                return Reject(MarketDataQualityStatus.Duplicate, "Event was already processed.");
            }

            if (state?.LastSequence is long lastSequence && marketEvent.Sequence is long sequence)
            {
                if (sequence <= lastSequence)
                {
                    return Reject(MarketDataQualityStatus.OutOfOrder, $"Sequence {sequence} is not greater than {lastSequence}.");
                }
                if (sequence > lastSequence + 1)
                {
                    return Gap($"Sequence gap detected: expected {lastSequence + 1}, received {sequence}.");
                }
            }

            if (state?.LastEventTimeUtc is DateTime lastEventTime && eventTime < ToUtc(lastEventTime))
            {
                return Reject(MarketDataQualityStatus.OutOfOrder, $"Event time {eventTime:O} is earlier than {ToUtc(lastEventTime):O}.");
            }

            if (marketEvent.Kind == MarketDataEventKind.Bar
                && state?.LastEventTimeUtc is DateTime lastBarTime
                && ExpectedBarInterval(marketEvent.Timeframe) is TimeSpan interval
                && ToUtc(lastBarTime).Date == eventTime.Date
                && eventTime > ToUtc(lastBarTime).Add(interval).Add(FutureTolerance))
            {
                return Gap($"Bar gap detected: expected {ToUtc(lastBarTime).Add(interval):O}, received {eventTime:O}.");
            }

            return new MarketDataQualityAssessment(MarketDataQualityStatus.Healthy, "Event passed canonical quality checks.", true, marketEvent.Kind == MarketDataEventKind.Bar);
        }

        private static string? ValidateShape(CanonicalMarketDataEvent marketEvent)
        {
            if (string.IsNullOrWhiteSpace(marketEvent.EventId)) return "EventId is required.";
            if (string.IsNullOrWhiteSpace(marketEvent.InstrumentId)) return "InstrumentId is required.";
            if (string.IsNullOrWhiteSpace(marketEvent.Symbol)) return "Symbol is required.";
            if (string.IsNullOrWhiteSpace(marketEvent.Source)) return "Source is required.";
            if (marketEvent.EventTimeUtc == default) return "EventTimeUtc is required.";
            if (marketEvent.ReceivedTimeUtc == default) return "ReceivedTimeUtc is required.";
            if (!Enum.IsDefined(marketEvent.Kind)) return "Market data event kind is unsupported.";
            if (marketEvent.Sequence is < 0) return "Sequence cannot be negative.";

            if (marketEvent.Kind == MarketDataEventKind.Bar)
            {
                if (ExpectedBarInterval(marketEvent.Timeframe) == null) return $"Unsupported bar timeframe '{marketEvent.Timeframe}'.";
                if (marketEvent.Open is not > 0m || marketEvent.High is not > 0m || marketEvent.Low is not > 0m || marketEvent.Close is not > 0m) return "OHLC prices must be positive.";
                if (marketEvent.High.Value < marketEvent.Low.Value) return "High is below low.";
                if (marketEvent.Open.Value > marketEvent.High.Value || marketEvent.Open.Value < marketEvent.Low.Value || marketEvent.Close.Value > marketEvent.High.Value || marketEvent.Close.Value < marketEvent.Low.Value) return "Open or close is outside high/low range.";
                if (marketEvent.Volume is < 0m) return "Volume cannot be negative.";
            }
            else
            {
                if (marketEvent.Price is not > 0m) return "Bid, ask and trade events require a positive price.";
                if (marketEvent.Size is < 0m) return "Event size cannot be negative.";
            }

            return null;
        }

        private static MarketDataQualityAssessment Reject(MarketDataQualityStatus status, string reason) => new(status, reason, false, false);
        private static MarketDataQualityAssessment Gap(string reason) => new(MarketDataQualityStatus.GapDetected, reason, true, false);

        private static MarketDataStreamStateRecord CreateState(CanonicalMarketDataEvent marketEvent, string streamKey, DateTime now) => new()
        {
            StreamKey = streamKey,
            InstrumentId = marketEvent.InstrumentId,
            Symbol = marketEvent.Symbol,
            Kind = marketEvent.Kind,
            Timeframe = marketEvent.Timeframe,
            Source = marketEvent.Source,
            UpdatedAtUtc = now,
            Version = 0
        };

        private static void ApplyAssessment(
            MarketDataStreamStateRecord state,
            CanonicalMarketDataEvent marketEvent,
            MarketDataQualityAssessment assessment,
            DateTime now)
        {
            state.InstrumentId = marketEvent.InstrumentId;
            state.Symbol = marketEvent.Symbol;
            state.Kind = marketEvent.Kind;
            state.Timeframe = marketEvent.Timeframe;
            state.Source = marketEvent.Source;
            state.Status = assessment.Status;
            state.StatusReason = assessment.Reason;
            state.IsHealthy = assessment.IsHealthy;
            state.LastReceivedTimeUtc = ToUtc(marketEvent.ReceivedTimeUtc);
            state.UpdatedAtUtc = now;
            state.Version++;

            if (assessment.IsHealthy || assessment.Status == MarketDataQualityStatus.GapDetected)
            {
                state.LastEventId = marketEvent.EventId;
                state.LastEventTimeUtc = ToUtc(marketEvent.EventTimeUtc);
                state.LastSequence = marketEvent.Sequence;
            }
        }

        private static TimeSpan? ExpectedBarInterval(string timeframe) => timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" or "1 min" or "1 minute" => TimeSpan.FromMinutes(1),
            "5m" or "5 mins" or "5 minutes" => TimeSpan.FromMinutes(5),
            "15m" or "15 mins" or "15 minutes" => TimeSpan.FromMinutes(15),
            _ => null
        };

        private static MarketDataStreamSnapshot ToSnapshot(MarketDataStreamStateRecord record) => new()
        {
            StreamKey = record.StreamKey,
            InstrumentId = record.InstrumentId,
            Symbol = record.Symbol,
            Kind = record.Kind,
            Timeframe = record.Timeframe,
            Source = record.Source,
            LastEventTimeUtc = record.LastEventTimeUtc.HasValue ? ToUtc(record.LastEventTimeUtc.Value) : null,
            LastReceivedTimeUtc = record.LastReceivedTimeUtc.HasValue ? ToUtc(record.LastReceivedTimeUtc.Value) : null,
            LastSequence = record.LastSequence,
            Status = record.Status,
            StatusReason = record.StatusReason,
            IsHealthy = record.IsHealthy,
            UpdatedAtUtc = ToUtc(record.UpdatedAtUtc),
            Version = record.Version
        };

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
