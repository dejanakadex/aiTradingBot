using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;
using DomainCandle = TradingBot.Domain.Models.Candle;

namespace TradingBot.Infrastructure.Services
{
    public sealed class DeterministicReplayService : IDeterministicReplayService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IMarketDatasetStore _datasetStore;
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IFeatureEngine _featureEngine;
        private readonly IPatternDetectorFactory _patternDetectorFactory;
        private readonly IClock _clock;
        private readonly ReplaySettings _settings;
        private readonly ILogger<DeterministicReplayService> _logger;
        private readonly SemaphoreSlim _processingGate = new(1, 1);

        public DeterministicReplayService(
            IMarketDatasetStore datasetStore,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IFeatureEngine featureEngine,
            IPatternDetectorFactory patternDetectorFactory,
            IClock clock,
            IOptions<ReplaySettings> settings,
            ILogger<DeterministicReplayService> logger)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
            _patternDetectorFactory = patternDetectorFactory ?? throw new ArgumentNullException(nameof(patternDetectorFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ReplayRunSnapshot> StartAsync(
            ReplayStartRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled) throw new InvalidOperationException("Deterministic replay is disabled by configuration.");
            if (request == null) throw new ArgumentNullException(nameof(request));
            var instrumentId = Required(request.InstrumentId, nameof(request.InstrumentId));
            var strategyId = Required(request.StrategyId, nameof(request.StrategyId));
            var fromUtc = ToUtc(request.FromUtc);
            var toUtc = ToUtc(request.ToUtc);
            if (fromUtc >= toUtc) throw new ArgumentException("Replay FromUtc must be earlier than ToUtc.", nameof(request));
            ValidateSpeed(request.SpeedMultiplier);

            var verification = await _datasetStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!verification.IsValid)
            {
                throw new InvalidDataException($"Dataset integrity verification failed: {string.Join(" ", verification.Errors)}");
            }

            var records = await ReadInputAsync(instrumentId, fromUtc, toUtc, cancellationToken).ConfigureAwait(false);
            if (records.Count == 0)
            {
                throw new InvalidOperationException("No bar events exist in the requested dataset range.");
            }

            var now = ToUtc(_clock.UtcNow);
            var run = new ReplayRunRecord
            {
                Id = Guid.NewGuid(),
                InstrumentId = instrumentId,
                Symbol = records.Select(item => item.Symbol).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))?.Trim().ToUpperInvariant() ?? instrumentId,
                StrategyId = strategyId,
                FromUtc = fromUtc,
                ToUtc = toUtc,
                SpeedMultiplier = request.SpeedMultiplier,
                Status = ReplayRunStatus.Pending,
                InputSha256 = ComputeInputHash(records),
                InputEventCount = records.Count,
                MarketDataVersion = PipelineContractVersions.MarketData,
                FeatureVersion = PipelineContractVersions.Features,
                PatternVersion = PipelineContractVersions.Patterns,
                StrategyVersion = PipelineContractVersions.Strategy,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            db.ReplayRunRecords.Add(run);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(run);
        }

        public async Task<ReplayRunSnapshot?> GetAsync(Guid replayRunId, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await db.ReplayRunRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == replayRunId, cancellationToken)
                .ConfigureAwait(false);
            return run == null ? null : ToSnapshot(run);
        }

        public async Task<IReadOnlyList<ReplayRunSnapshot>> GetAllAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.ReplayRunRecords.AsNoTracking()
                .OrderByDescending(item => item.CreatedAtUtc)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<IReadOnlyList<ReplaySignalSnapshot>> GetSignalsAsync(
            Guid replayRunId,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.ReplaySignalRecords.AsNoTracking()
                .Where(item => item.ReplayRunId == replayRunId)
                .OrderBy(item => item.DetectedAtUtc)
                .ThenBy(item => item.SignalId)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public Task<ReplayRunSnapshot> PauseAsync(Guid replayRunId, CancellationToken cancellationToken = default) =>
            ChangeStatusAsync(replayRunId, ReplayRunStatus.Paused, null, cancellationToken);

        public Task<ReplayRunSnapshot> ResumeAsync(
            Guid replayRunId,
            decimal? speedMultiplier = null,
            CancellationToken cancellationToken = default) =>
            ChangeStatusAsync(replayRunId, ReplayRunStatus.Running, speedMultiplier, cancellationToken);

        public Task<ReplayRunSnapshot> CancelAsync(Guid replayRunId, CancellationToken cancellationToken = default) =>
            ChangeStatusAsync(replayRunId, ReplayRunStatus.Cancelled, null, cancellationToken);

        public async Task<IReadOnlyList<Guid>> GetRunnableRunIdsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return await db.ReplayRunRecords.AsNoTracking()
                .Where(item => item.Status == ReplayRunStatus.Pending || item.Status == ReplayRunStatus.Running)
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => item.Id)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<ReplayRunSnapshot> ProcessBatchAsync(
            Guid replayRunId,
            int? maximumEvents = null,
            CancellationToken cancellationToken = default)
        {
            var limit = maximumEvents ?? _settings.EventsPerBatch;
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEvents), "Replay batch size must be greater than zero.");

            await _processingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ProcessBatchCoreAsync(replayRunId, limit, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _processingGate.Release();
            }
        }

        private async Task<ReplayRunSnapshot> ProcessBatchCoreAsync(Guid replayRunId, int limit, CancellationToken cancellationToken)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await db.ReplayRunRecords.SingleOrDefaultAsync(item => item.Id == replayRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Replay run '{replayRunId}' was not found.");
            if (run.Status is ReplayRunStatus.Paused or ReplayRunStatus.Completed or ReplayRunStatus.Cancelled or ReplayRunStatus.Faulted)
            {
                return ToSnapshot(run);
            }

            try
            {
                var records = await ReadInputAsync(run.InstrumentId, run.FromUtc, run.ToUtc, cancellationToken).ConfigureAwait(false);
                var inputHash = ComputeInputHash(records);
                if (records.Count != run.InputEventCount || !inputHash.Equals(run.InputSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Replay input changed after the run was created; refusing a non-reproducible resume.");
                }
                if (run.ProcessedEventCount < 0 || run.ProcessedEventCount > records.Count)
                {
                    throw new InvalidDataException("Replay checkpoint is outside the verified input range.");
                }

                if (run.Status == ReplayRunStatus.Pending)
                {
                    run.Status = ReplayRunStatus.Running;
                    run.StartedAtUtc ??= ToUtc(_clock.UtcNow);
                    Touch(run);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                var detector = _patternDetectorFactory.Create();
                var histories = new Dictionary<Timeframe, List<DomainCandle>>();
                DateTime? previousEventTimeUtc = null;
                var processedThisBatch = 0;

                for (var index = 0; index < records.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = records[index];
                    var isWarmup = index < run.ProcessedEventCount;

                    var candle = TryCreateCandle(record);
                    if (candle != null)
                    {
                        var history = histories.GetValueOrDefault(candle.Timeframe);
                        if (history == null)
                        {
                            history = new List<DomainCandle>(_settings.HistoryCandles);
                            histories[candle.Timeframe] = history;
                        }
                        history.Add(candle);
                        if (history.Count > _settings.HistoryCandles)
                        {
                            history.RemoveAt(0);
                        }

                        var features = _featureEngine.ComputeFeatures(history);
                        var patterns = detector.Detect(history);
                        if (!isWarmup)
                        {
                            await DelayForSpeedAsync(previousEventTimeUtc, record.EventTimeUtc, run.SpeedMultiplier, cancellationToken).ConfigureAwait(false);
                            foreach (var pattern in patterns)
                            {
                                await PersistSignalAsync(db, run, record.EventId, pattern, features, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    previousEventTimeUtc = record.EventTimeUtc;
                    if (isWarmup) continue;

                    await db.Entry(run).ReloadAsync(cancellationToken).ConfigureAwait(false);
                    if (run.Status != ReplayRunStatus.Running)
                    {
                        return ToSnapshot(run);
                    }

                    run.ProcessedEventCount = index + 1;
                    run.CheckpointEventId = record.EventId;
                    run.CheckpointEventTimeUtc = record.EventTimeUtc;
                    run.CheckpointReceivedTimeUtc = record.ReceivedTimeUtc;
                    var persistedSignalCount = await db.ReplaySignalRecords.CountAsync(item => item.ReplayRunId == run.Id, cancellationToken).ConfigureAwait(false);
                    var pendingSignalCount = db.ChangeTracker.Entries<ReplaySignalRecord>()
                        .Count(entry => entry.State == EntityState.Added && entry.Entity.ReplayRunId == run.Id);
                    run.SignalCount = persistedSignalCount + pendingSignalCount;
                    Touch(run);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    processedThisBatch++;
                    if (processedThisBatch >= limit) break;
                }

                if (run.ProcessedEventCount == records.Count)
                {
                    run.Status = ReplayRunStatus.Completed;
                    run.CompletedAtUtc = ToUtc(_clock.UtcNow);
                    run.SignalCount = await db.ReplaySignalRecords.CountAsync(item => item.ReplayRunId == run.Id, cancellationToken).ConfigureAwait(false);
                    run.OutputSha256 = await ComputeOutputHashAsync(db, run.Id, cancellationToken).ConfigureAwait(false);
                    Touch(run);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                return ToSnapshot(run);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (DbUpdateConcurrencyException)
            {
                await db.Entry(run).ReloadAsync(cancellationToken).ConfigureAwait(false);
                return ToSnapshot(run);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deterministic replay {ReplayRunId} failed.", replayRunId);
                await db.Entry(run).ReloadAsync(cancellationToken).ConfigureAwait(false);
                run.Status = ReplayRunStatus.Faulted;
                run.LastError = ex.Message;
                Touch(run);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ToSnapshot(run);
            }
        }

        private async Task<ReplayRunSnapshot> ChangeStatusAsync(
            Guid replayRunId,
            ReplayRunStatus target,
            decimal? speedMultiplier,
            CancellationToken cancellationToken)
        {
            if (speedMultiplier.HasValue) ValidateSpeed(speedMultiplier.Value);
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var run = await db.ReplayRunRecords.SingleOrDefaultAsync(item => item.Id == replayRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Replay run '{replayRunId}' was not found.");

            var allowed = target switch
            {
                ReplayRunStatus.Paused => run.Status is ReplayRunStatus.Pending or ReplayRunStatus.Running,
                ReplayRunStatus.Running => run.Status is ReplayRunStatus.Paused or ReplayRunStatus.Pending,
                ReplayRunStatus.Cancelled => run.Status is ReplayRunStatus.Pending or ReplayRunStatus.Running or ReplayRunStatus.Paused,
                _ => false
            };
            if (!allowed) throw new InvalidOperationException($"Replay run in status {run.Status} cannot transition to {target}.");

            run.Status = target;
            if (speedMultiplier.HasValue) run.SpeedMultiplier = speedMultiplier.Value;
            if (target == ReplayRunStatus.Running) run.StartedAtUtc ??= ToUtc(_clock.UtcNow);
            if (target == ReplayRunStatus.Cancelled) run.CompletedAtUtc = ToUtc(_clock.UtcNow);
            Touch(run);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(run);
        }

        private async Task PersistSignalAsync(
            TradingBotDbContext db,
            ReplayRunRecord run,
            string sourceEventId,
            PatternCandidate pattern,
            MarketFeatures features,
            CancellationToken cancellationToken)
        {
            var context = PipelineContext.CreateForSignal(
                run.InstrumentId,
                $"{pattern.PatternType}|{pattern.Timeframe}|{pattern.DetectedAtUtc:O}",
                run.StrategyId);
            if (db.ReplaySignalRecords.Local.Any(item => item.ReplayRunId == run.Id && item.SignalId == context.SignalId)
                || await db.ReplaySignalRecords.AnyAsync(
                    item => item.ReplayRunId == run.Id && item.SignalId == context.SignalId,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var metadata = new SortedDictionary<string, string>(pattern.Metadata, StringComparer.Ordinal);
            db.ReplaySignalRecords.Add(new ReplaySignalRecord
            {
                ReplayRunId = run.Id,
                SignalId = context.SignalId,
                SourceEventId = sourceEventId,
                PatternType = pattern.PatternType,
                Symbol = pattern.Symbol,
                Timeframe = pattern.Timeframe,
                DetectedAtUtc = pattern.DetectedAtUtc,
                Confidence = pattern.Confidence,
                RelevantPriceLevelsJson = JsonSerializer.Serialize(pattern.RelevantPriceLevels, JsonOptions),
                MetadataJson = JsonSerializer.Serialize(metadata, JsonOptions),
                FeaturesJson = JsonSerializer.Serialize(features, JsonOptions),
                CreatedAtUtc = ToUtc(_clock.UtcNow)
            });
        }

        private async Task DelayForSpeedAsync(
            DateTime? previousEventTimeUtc,
            DateTime eventTimeUtc,
            decimal speedMultiplier,
            CancellationToken cancellationToken)
        {
            if (speedMultiplier <= 0m || !previousEventTimeUtc.HasValue || _settings.MaximumDelayMilliseconds == 0) return;
            var eventMilliseconds = (decimal)Math.Max(0d, (eventTimeUtc - previousEventTimeUtc.Value).TotalMilliseconds);
            var delayMilliseconds = (int)Math.Min(_settings.MaximumDelayMilliseconds, decimal.ToInt32(decimal.Min(int.MaxValue, eventMilliseconds / speedMultiplier)));
            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task<IReadOnlyList<MarketDatasetRecord>> ReadInputAsync(
            string instrumentId,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken cancellationToken)
        {
            var records = await _datasetStore.ReadAsync(
                new DatasetQuery(instrumentId, fromUtc, toUtc, new[] { "bar" }),
                cancellationToken).ConfigureAwait(false);
            return records
                .GroupBy(item => item.EventId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.ReceivedTimeUtc).First())
                .OrderBy(item => item.EventTimeUtc)
                .ThenBy(item => item.ReceivedTimeUtc)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToArray();
        }

        private static DomainCandle? TryCreateCandle(MarketDatasetRecord record)
        {
            if (!record.IsFinal || !record.CanTriggerTrading
                || !Enum.TryParse<MarketDataQualityStatus>(record.QualityStatus, true, out var quality)
                || quality != MarketDataQualityStatus.Healthy
                || !TryParseTimeframe(record.Timeframe, out var timeframe)
                || !record.Open.HasValue || !record.High.HasValue || !record.Low.HasValue || !record.Close.HasValue || !record.Volume.HasValue)
            {
                return null;
            }

            return new DomainCandle(
                record.Symbol,
                timeframe,
                record.EventTimeUtc,
                record.Open.Value,
                record.High.Value,
                record.Low.Value,
                record.Close.Value,
                record.Volume.Value,
                record.InstrumentId,
                record.ReceivedTimeUtc,
                record.Source,
                record.IsFinal,
                quality);
        }

        private static bool TryParseTimeframe(string value, out Timeframe timeframe)
        {
            if (Enum.TryParse(value, true, out timeframe)) return true;
            return value.Trim().ToLowerInvariant() switch
            {
                "1m" => Set(Timeframe.OneMinute, out timeframe),
                "5m" => Set(Timeframe.FiveMinutes, out timeframe),
                "15m" => Set(Timeframe.FifteenMinutes, out timeframe),
                "1h" => Set(Timeframe.OneHour, out timeframe),
                "4h" => Set(Timeframe.FourHour, out timeframe),
                "1d" => Set(Timeframe.Daily, out timeframe),
                _ => false
            };
        }

        private static bool Set(Timeframe value, out Timeframe timeframe)
        {
            timeframe = value;
            return true;
        }

        private static string ComputeInputHash(IReadOnlyList<MarketDatasetRecord> records)
        {
            var canonical = new StringBuilder(records.Count * 160);
            foreach (var item in records)
            {
                canonical.Append(item.EventId).Append('|')
                    .Append(item.InstrumentId).Append('|')
                    .Append(item.Symbol).Append('|')
                    .Append(item.DataType).Append('|')
                    .Append(item.EventTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
                    .Append(item.IsFinal).Append('|').Append(item.Timeframe).Append('|')
                    .Append(Decimal(item.Open)).Append('|').Append(Decimal(item.High)).Append('|')
                    .Append(Decimal(item.Low)).Append('|').Append(Decimal(item.Close)).Append('|')
                    .Append(Decimal(item.Volume)).Append('|').Append(item.QualityStatus).Append('|')
                    .Append(item.CanTriggerTrading).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        }

        private static async Task<string> ComputeOutputHashAsync(
            TradingBotDbContext db,
            Guid replayRunId,
            CancellationToken cancellationToken)
        {
            var signals = await db.ReplaySignalRecords.AsNoTracking()
                .Where(item => item.ReplayRunId == replayRunId)
                .OrderBy(item => item.DetectedAtUtc)
                .ThenBy(item => item.SignalId)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var canonical = new StringBuilder(signals.Length * 160);
            foreach (var item in signals)
            {
                canonical.Append(item.SignalId.ToString("D")).Append('|')
                    .Append(item.SourceEventId).Append('|').Append(item.PatternType).Append('|')
                    .Append(item.Symbol).Append('|').Append(item.Timeframe).Append('|')
                    .Append(item.DetectedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Confidence.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.RelevantPriceLevelsJson).Append('|').Append(item.MetadataJson).Append('|')
                    .Append(item.FeaturesJson).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        }

        private void ValidateSpeed(decimal speedMultiplier)
        {
            if (speedMultiplier < 0m || speedMultiplier > _settings.MaximumSpeedMultiplier)
            {
                throw new ArgumentOutOfRangeException(nameof(speedMultiplier), $"Replay speed must be 0 (maximum) or between 0 and {_settings.MaximumSpeedMultiplier}.");
            }
        }

        private void Touch(ReplayRunRecord run)
        {
            run.UpdatedAtUtc = ToUtc(_clock.UtcNow);
            run.Version++;
        }

        private static string Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private static ReplayRunSnapshot ToSnapshot(ReplayRunRecord item) => new(
            item.Id, item.InstrumentId, item.Symbol, item.StrategyId, item.FromUtc, item.ToUtc,
            item.SpeedMultiplier, item.Status, item.InputSha256, item.InputEventCount,
            item.ProcessedEventCount, item.SignalCount, item.CheckpointEventId,
            item.CheckpointEventTimeUtc, item.OutputSha256, item.MarketDataVersion,
            item.FeatureVersion, item.PatternVersion, item.StrategyVersion, item.LastError,
            item.CreatedAtUtc, item.StartedAtUtc, item.UpdatedAtUtc, item.CompletedAtUtc);

        private static ReplaySignalSnapshot ToSnapshot(ReplaySignalRecord item) => new(
            item.Id, item.ReplayRunId, item.SignalId, item.SourceEventId, item.PatternType,
            item.Symbol, item.Timeframe, item.DetectedAtUtc, item.Confidence,
            item.RelevantPriceLevelsJson, item.MetadataJson, item.FeaturesJson);
    }
}
