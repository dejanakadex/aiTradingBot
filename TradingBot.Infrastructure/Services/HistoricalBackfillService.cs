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
    public sealed class HistoricalBackfillService : IHistoricalBackfillService
    {
        private const string Trigger = "HistoricalBackfill";
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IMarketDataService _marketData;
        private readonly IInstrumentRegistryService _registry;
        private readonly HistoricalBackfillSettings _settings;
        private readonly IClock _clock;
        private readonly IMarketDatasetSink? _datasetSink;
        private readonly ILogger<HistoricalBackfillService> _logger;
        private readonly SemaphoreSlim _singleRequest = new(1, 1);

        public HistoricalBackfillService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IMarketDataService marketData,
            IInstrumentRegistryService registry,
            IOptions<HistoricalBackfillSettings> settings,
            IClock clock,
            ILogger<HistoricalBackfillService> logger,
            IMarketDatasetSink? datasetSink = null)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _marketData = marketData ?? throw new ArgumentNullException(nameof(marketData));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _datasetSink = datasetSink;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task SynchronizePlanAsync(CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled) return;

            var instruments = await _registry.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var now = AlignDown(ToUtc(_clock.UtcNow), TimeSpan.FromMinutes(1));
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            foreach (var instrument in instruments.Where(item => item.ConfiguredEnabled))
            {
                foreach (var timeframe in instrument.MarketDataTimeframes)
                {
                    var normalized = NormalizeTimeframe(timeframe);
                    var timeframeSettings = _settings.GetTimeframe(normalized);
                    var desiredEnd = AlignDown(now, GetInterval(normalized));
                    var desiredStart = desiredEnd.AddDays(-timeframeSettings.LookbackDays);
                    var job = await db.HistoricalBackfillJobRecords.SingleOrDefaultAsync(
                        item => item.InstrumentId == instrument.InstrumentId && item.Timeframe == normalized,
                        cancellationToken).ConfigureAwait(false);

                    if (job == null)
                    {
                        db.HistoricalBackfillJobRecords.Add(new HistoricalBackfillJobRecord
                        {
                            InstrumentId = instrument.InstrumentId,
                            Symbol = instrument.Symbol,
                            Timeframe = normalized,
                            Status = HistoricalBackfillStatus.Pending,
                            DesiredStartUtc = desiredStart,
                            DesiredEndUtc = desiredEnd,
                            NextSegmentEndUtc = desiredEnd,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                            Version = 1
                        });
                        continue;
                    }

                    var resetRequested = instrument.Status == InstrumentOnboardingStatus.BackfillPending
                        && (job.Status != HistoricalBackfillStatus.Pending
                            || !string.Equals(job.Symbol, instrument.Symbol, StringComparison.OrdinalIgnoreCase));
                    if (resetRequested)
                    {
                        job.Symbol = instrument.Symbol;
                        job.Status = HistoricalBackfillStatus.Pending;
                        job.DesiredStartUtc = desiredStart;
                        job.DesiredEndUtc = desiredEnd;
                        job.NextSegmentEndUtc = desiredEnd;
                        job.NextAttemptUtc = null;
                        job.CompletedSegments = 0;
                        job.FailedAttempts = 0;
                        job.BarsReceived = 0;
                        job.BarsInserted = 0;
                        job.DuplicateBars = 0;
                        job.GapCount = 0;
                        job.LastError = string.Empty;
                        job.UpdatedAtUtc = now;
                        job.Version++;
                    }
                }
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<HistoricalBackfillJobSnapshot?> RunNextSegmentAsync(CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled) return null;

            await _singleRequest.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = ToUtc(_clock.UtcNow);
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var lastRequest = await db.HistoricalBackfillJobRecords
                    .MaxAsync(item => (DateTime?)item.LastRequestedAtUtc, cancellationToken)
                    .ConfigureAwait(false);
                if (lastRequest.HasValue && now < lastRequest.Value.AddMilliseconds(_settings.PacingDelayMilliseconds))
                {
                    return null;
                }

                var job = await db.HistoricalBackfillJobRecords
                    .Where(item => item.Status == HistoricalBackfillStatus.Pending
                        || item.Status == HistoricalBackfillStatus.Running
                        || (item.Status == HistoricalBackfillStatus.RetryPending
                            && (item.NextAttemptUtc == null || item.NextAttemptUtc <= now)))
                    .OrderBy(item => item.Timeframe == "1m" ? 0 : item.Timeframe == "5m" ? 1 : 2)
                    .ThenBy(item => item.UpdatedAtUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (job == null) return null;

                await EnsureInstrumentBackfillingAsync(job.InstrumentId, cancellationToken).ConfigureAwait(false);

                var segmentDays = _settings.GetTimeframe(job.Timeframe).SegmentDays;
                var segmentEnd = job.NextSegmentEndUtc;
                var segmentStart = segmentEnd.AddDays(-segmentDays);
                if (segmentStart < job.DesiredStartUtc) segmentStart = job.DesiredStartUtc;

                var segment = await db.HistoricalBackfillSegmentRecords.SingleOrDefaultAsync(
                    item => item.JobId == job.Id && item.StartUtc == segmentStart && item.EndUtc == segmentEnd,
                    cancellationToken).ConfigureAwait(false);
                if (segment == null)
                {
                    segment = new HistoricalBackfillSegmentRecord
                    {
                        JobId = job.Id,
                        InstrumentId = job.InstrumentId,
                        Symbol = job.Symbol,
                        Timeframe = job.Timeframe,
                        StartUtc = segmentStart,
                        EndUtc = segmentEnd
                    };
                    db.HistoricalBackfillSegmentRecords.Add(segment);
                }

                segment.Status = HistoricalBackfillSegmentStatus.Running;
                segment.AttemptCount++;
                segment.RequestedAtUtc = now;
                segment.LastError = string.Empty;
                job.Status = HistoricalBackfillStatus.Running;
                job.LastRequestedAtUtc = now;
                job.NextAttemptUtc = null;
                job.UpdatedAtUtc = now;
                job.Version++;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    var request = new HistoricalBarRequest(job.InstrumentId, job.Symbol, job.Timeframe, segmentStart, segmentEnd);
                    var received = (await _marketData.GetHistoricalBarsAsync(request, cancellationToken).ConfigureAwait(false)).ToArray();
                    var valid = received
                        .Where(bar => IsValid(bar, job.Symbol, job.Timeframe, segmentStart, segmentEnd))
                        .GroupBy(bar => ToUtc(bar.TimestampUtc))
                        .Select(group => group.First())
                        .OrderBy(bar => bar.TimestampUtc)
                        .ToArray();
                    var inserted = await InsertBarsAsync(db, job, valid, now, cancellationToken).ConfigureAwait(false);
                    await RecordDatasetAsync(job, valid, now, cancellationToken).ConfigureAwait(false);

                    segment.Status = HistoricalBackfillSegmentStatus.Completed;
                    segment.BarsReceived = received.Length;
                    segment.BarsInserted = inserted;
                    segment.DuplicateBars = Math.Max(0, valid.Length - inserted);
                    segment.CompletedAtUtc = now;
                    job.CompletedSegments++;
                    job.BarsReceived += received.Length;
                    job.BarsInserted += inserted;
                    job.DuplicateBars += segment.DuplicateBars;
                    job.NextSegmentEndUtc = segmentStart;
                    job.LastError = string.Empty;
                    job.UpdatedAtUtc = now;
                    job.Version++;

                    if (job.NextSegmentEndUtc <= job.DesiredStartUtc)
                    {
                        var gapCount = await RebuildGapReportAsync(db, job, now, cancellationToken).ConfigureAwait(false);
                        job.GapCount = gapCount;
                        job.Status = gapCount == 0
                            ? HistoricalBackfillStatus.Completed
                            : HistoricalBackfillStatus.CompletedWithGaps;
                    }
                    else
                    {
                        job.Status = HistoricalBackfillStatus.Pending;
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (job.Status is HistoricalBackfillStatus.Completed or HistoricalBackfillStatus.CompletedWithGaps)
                    {
                        await TryCompleteInstrumentAsync(job.InstrumentId, cancellationToken).ConfigureAwait(false);
                    }

                    return ToSnapshot(job);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var terminal = segment.AttemptCount >= _settings.MaximumAttemptsPerSegment;
                    segment.Status = terminal ? HistoricalBackfillSegmentStatus.Faulted : HistoricalBackfillSegmentStatus.RetryPending;
                    segment.LastError = ex.Message;
                    job.Status = terminal ? HistoricalBackfillStatus.Faulted : HistoricalBackfillStatus.RetryPending;
                    job.FailedAttempts++;
                    job.LastError = ex.Message;
                    job.NextAttemptUtc = terminal ? null : now.Add(CalculateRetryDelay(segment.AttemptCount));
                    job.UpdatedAtUtc = now;
                    job.Version++;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    if (terminal) await TryFaultInstrumentAsync(job.InstrumentId, ex.Message, cancellationToken).ConfigureAwait(false);
                    _logger.LogWarning(ex, "Historical backfill segment failed for {InstrumentId} {Timeframe} [{StartUtc}, {EndUtc}); terminal={Terminal}.", job.InstrumentId, job.Timeframe, segmentStart, segmentEnd, terminal);
                    return ToSnapshot(job);
                }
            }
            finally
            {
                _singleRequest.Release();
            }
        }

        private async Task RecordDatasetAsync(
            HistoricalBackfillJobRecord job,
            IEnumerable<MarketBar> bars,
            DateTime receivedAtUtc,
            CancellationToken cancellationToken)
        {
            if (_datasetSink == null) return;
            foreach (var bar in bars)
            {
                var canonical = new CanonicalMarketDataEvent
                {
                    EventId = CanonicalMarketDataIdentity.ForBar(job.InstrumentId, bar, job.Timeframe),
                    InstrumentId = job.InstrumentId,
                    Symbol = job.Symbol,
                    Kind = MarketDataEventKind.Bar,
                    EventTimeUtc = ToUtc(bar.TimestampUtc),
                    ReceivedTimeUtc = ToUtc(bar.ReceivedTimeUtc ?? receivedAtUtc),
                    Source = string.IsNullOrWhiteSpace(bar.Source) ? "IBKR.HistoricalBackfill" : bar.Source.Trim(),
                    Sequence = bar.Sequence,
                    IsFinal = bar.IsFinal,
                    Timeframe = job.Timeframe,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume
                };
                try
                {
                    var accepted = await _datasetSink.EnqueueAsync(
                        MarketDatasetRecord.From(
                            canonical,
                            new MarketDataQualityAssessment(MarketDataQualityStatus.Healthy, "Historical bar passed backfill validation.", true, false),
                            string.Empty),
                        cancellationToken).ConfigureAwait(false);
                    if (!accepted)
                    {
                        _logger.LogWarning("Dataset writer rejected historical market-data event {EventId}.", canonical.EventId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enqueue historical market-data event {EventId} for dataset storage.", canonical.EventId);
                }
            }
        }

        public async Task<IReadOnlyList<HistoricalBackfillJobSnapshot>> GetJobsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var jobs = await db.HistoricalBackfillJobRecords.AsNoTracking()
                .OrderBy(item => item.Symbol).ThenBy(item => item.Timeframe)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return jobs.Select(ToSnapshot).ToArray();
        }

        public async Task<IReadOnlyList<HistoricalDataGapSnapshot>> GetGapsAsync(string? instrumentId = null, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.HistoricalDataGapRecords.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(instrumentId))
            {
                var normalized = instrumentId.Trim();
                query = query.Where(item => item.InstrumentId == normalized);
            }

            var gaps = await query.OrderBy(item => item.InstrumentId).ThenBy(item => item.Timeframe).ThenBy(item => item.StartUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return gaps.Select(item => new HistoricalDataGapSnapshot(item.Id, item.InstrumentId, item.Symbol, item.Timeframe, ToUtc(item.StartUtc), ToUtc(item.EndUtc), item.MissingBars, ToUtc(item.DetectedAtUtc))).ToArray();
        }

        private async Task EnsureInstrumentBackfillingAsync(string instrumentId, CancellationToken cancellationToken)
        {
            var instrument = await _registry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false);
            if (instrument?.Status == InstrumentOnboardingStatus.BackfillPending)
            {
                await _registry.TransitionAsync(instrumentId, InstrumentOnboardingStatus.BackfillPending, InstrumentOnboardingStatus.Backfilling,
                    "Historical backfill started.", Trigger, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task TryCompleteInstrumentAsync(string instrumentId, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var hasIncomplete = await db.HistoricalBackfillJobRecords.AnyAsync(item => item.InstrumentId == instrumentId
                && item.Status != HistoricalBackfillStatus.Completed
                && item.Status != HistoricalBackfillStatus.CompletedWithGaps, cancellationToken).ConfigureAwait(false);
            if (hasIncomplete) return;

            var instrument = await _registry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false);
            if (instrument?.Status == InstrumentOnboardingStatus.Backfilling)
            {
                await _registry.TransitionAsync(instrumentId, InstrumentOnboardingStatus.Backfilling, InstrumentOnboardingStatus.Collecting,
                    "Historical backfill completed for all configured timeframes; gap report is available.", Trigger, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task TryFaultInstrumentAsync(string instrumentId, string error, CancellationToken cancellationToken)
        {
            var instrument = await _registry.GetAsync(instrumentId, cancellationToken).ConfigureAwait(false);
            if (instrument?.Status == InstrumentOnboardingStatus.Backfilling)
            {
                await _registry.TransitionAsync(instrumentId, InstrumentOnboardingStatus.Backfilling, InstrumentOnboardingStatus.Faulted,
                    $"Historical backfill exhausted retries: {error}", Trigger, cancellationToken).ConfigureAwait(false);
            }
        }

        private static async Task<int> InsertBarsAsync(
            TradingBotDbContext db,
            HistoricalBackfillJobRecord job,
            IReadOnlyCollection<MarketBar> bars,
            DateTime receivedAtUtc,
            CancellationToken cancellationToken)
        {
            var inserted = 0;
            var timeframe = ToTimeframe(job.Timeframe);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var bar in bars)
            {
                inserted += await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT OR IGNORE INTO Candles
                        (InstrumentId, Symbol, Timeframe, TimestampUtc, ReceivedTimeUtc, Source, IsFinal, QualityStatus, Open, High, Low, Close, Volume)
                    VALUES
                        ({job.InstrumentId}, {job.Symbol}, {(int)timeframe}, {ToUtc(bar.TimestampUtc)}, {receivedAtUtc}, {"IBKR.HistoricalBackfill"}, {true}, {(int)MarketDataQualityStatus.Healthy}, {bar.Open}, {bar.High}, {bar.Low}, {bar.Close}, {bar.Volume})
                    """, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted;
        }

        private async Task<int> RebuildGapReportAsync(TradingBotDbContext db, HistoricalBackfillJobRecord job, DateTime now, CancellationToken cancellationToken)
        {
            await db.HistoricalDataGapRecords.Where(item => item.JobId == job.Id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            var timeframe = ToTimeframe(job.Timeframe);
            var timestamps = await db.Candles.AsNoTracking()
                .Where(item => item.Symbol == job.Symbol && item.Timeframe == timeframe
                    && item.TimestampUtc >= job.DesiredStartUtc && item.TimestampUtc < job.DesiredEndUtc)
                .OrderBy(item => item.TimestampUtc)
                .Select(item => item.TimestampUtc)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var interval = GetInterval(job.Timeframe);
            var eastern = ResolveEasternTimeZone();
            var gaps = new List<HistoricalDataGapRecord>();

            foreach (var day in timestamps.Select(ToUtc).GroupBy(value => TimeZoneInfo.ConvertTimeFromUtc(value, eastern).Date))
            {
                var ordered = day.OrderBy(value => value).ToArray();
                for (var index = 1; index < ordered.Length; index++)
                {
                    var delta = ordered[index] - ordered[index - 1];
                    var missing = (int)(delta.Ticks / interval.Ticks) - 1;
                    if (missing <= 0) continue;
                    gaps.Add(NewGap(job, ordered[index - 1].Add(interval), ordered[index], missing, now));
                }
            }

            if (timestamps.Count == 0)
            {
                var estimated = Math.Max(1, (int)((job.DesiredEndUtc - job.DesiredStartUtc).Ticks / interval.Ticks));
                gaps.Add(NewGap(job, job.DesiredStartUtc, job.DesiredEndUtc, estimated, now));
            }

            db.HistoricalDataGapRecords.AddRange(gaps);
            return gaps.Count;
        }

        private static HistoricalDataGapRecord NewGap(HistoricalBackfillJobRecord job, DateTime start, DateTime end, int missing, DateTime now) => new()
        {
            JobId = job.Id,
            InstrumentId = job.InstrumentId,
            Symbol = job.Symbol,
            Timeframe = job.Timeframe,
            StartUtc = ToUtc(start),
            EndUtc = ToUtc(end),
            MissingBars = missing,
            DetectedAtUtc = now
        };

        private TimeSpan CalculateRetryDelay(int attempt)
        {
            var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
            return TimeSpan.FromSeconds(Math.Min(900, _settings.InitialRetryDelaySeconds * multiplier));
        }

        private static bool IsValid(MarketBar bar, string symbol, string timeframe, DateTime start, DateTime end)
        {
            var at = ToUtc(bar.TimestampUtc);
            return string.Equals(bar.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                && NormalizeTimeframe(bar.Timeframe) == timeframe
                && at >= start && at < end
                && bar.IsFinal
                && bar.Open > 0m && bar.High > 0m && bar.Low > 0m && bar.Close > 0m
                && bar.High >= bar.Low && bar.High >= bar.Open && bar.High >= bar.Close
                && bar.Low <= bar.Open && bar.Low <= bar.Close
                && bar.Volume >= 0m;
        }

        private static HistoricalBackfillJobSnapshot ToSnapshot(HistoricalBackfillJobRecord job) => new(
            job.Id, job.InstrumentId, job.Symbol, job.Timeframe, job.Status,
            ToUtc(job.DesiredStartUtc), ToUtc(job.DesiredEndUtc), ToUtc(job.NextSegmentEndUtc),
            job.NextAttemptUtc.HasValue ? ToUtc(job.NextAttemptUtc.Value) : null,
            job.CompletedSegments, job.FailedAttempts, job.BarsReceived, job.BarsInserted,
            job.DuplicateBars, job.GapCount, job.LastError, ToUtc(job.UpdatedAtUtc));

        private static string NormalizeTimeframe(string timeframe) => timeframe.Trim().ToLowerInvariant() switch
        {
            "1m" or "1 min" or "1 minute" => "1m",
            "5m" or "5 mins" or "5 minutes" => "5m",
            "15m" or "15 mins" or "15 minutes" => "15m",
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unsupported historical timeframe.")
        };

        private static TimeSpan GetInterval(string timeframe) => NormalizeTimeframe(timeframe) switch
        {
            "1m" => TimeSpan.FromMinutes(1),
            "5m" => TimeSpan.FromMinutes(5),
            "15m" => TimeSpan.FromMinutes(15),
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        private static Timeframe ToTimeframe(string timeframe) => NormalizeTimeframe(timeframe) switch
        {
            "1m" => Timeframe.OneMinute,
            "5m" => Timeframe.FiveMinutes,
            "15m" => Timeframe.FifteenMinutes,
            _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
        };

        private static DateTime AlignDown(DateTime value, TimeSpan interval)
        {
            var utc = ToUtc(value);
            return new DateTime(utc.Ticks - (utc.Ticks % interval.Ticks), DateTimeKind.Utc);
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private static TimeZoneInfo ResolveEasternTimeZone()
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
            catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        }
    }
}
