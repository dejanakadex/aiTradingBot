using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    public sealed partial class DeterministicReplayService : IDeterministicReplayService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IMarketDatasetStore _datasetStore;
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IFeatureEngine _featureEngine;
        private readonly IPatternDetectorFactory _patternDetectorFactory;
        private readonly IClock _clock;
        private readonly ReplaySettings _settings;
        private readonly ILogger<DeterministicReplayService> _logger;
        private readonly ICandidateResearchService? _candidateResearchService;
        private readonly TradingSettings _tradingSettings;
        private readonly ShortSignalSettings _shortSignalSettings;
        private readonly ShortAggregateSettings _shortAggregateSettings;
        private readonly SemaphoreSlim _processingGate = new(1, 1);

        public DeterministicReplayService(
            IMarketDatasetStore datasetStore,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IFeatureEngine featureEngine,
            IPatternDetectorFactory patternDetectorFactory,
            IClock clock,
            IOptions<ReplaySettings> settings,
            ILogger<DeterministicReplayService> logger,
            ICandidateResearchService? candidateResearchService = null,
            IOptions<TradingSettings>? tradingSettings = null,
            IOptions<ShortSignalSettings>? shortSignalSettings = null,
            IOptions<ShortAggregateSettings>? shortAggregateSettings = null,
            ForexResearchContract? forexContract = null, ForexTradeSimulator? forexSimulator = null,
            IOptions<TradingBot.Infrastructure.Options.ForexSignalSettings>? forexSignals = null,
            IOptions<TradingBot.Infrastructure.Options.ForexPatternSettings>? forexPatterns = null)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
            _patternDetectorFactory = patternDetectorFactory ?? throw new ArgumentNullException(nameof(patternDetectorFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _candidateResearchService = candidateResearchService;
            _tradingSettings = tradingSettings?.Value ?? new TradingSettings();
            _shortSignalSettings = shortSignalSettings?.Value ?? new ShortSignalSettings();
            _shortAggregateSettings = shortAggregateSettings?.Value ?? new ShortAggregateSettings();
            _forexContract = forexContract; _forexSimulator = forexSimulator;
            _forexSignals = forexSignals?.Value ?? new(); _forexPatterns = forexPatterns?.Value ?? new();
        }

        public async Task<ReplayRunSnapshot> StartAsync(
            ReplayStartRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled) throw new InvalidOperationException("Deterministic replay is disabled by configuration.");
            if (request == null) throw new ArgumentNullException(nameof(request));
            var instrumentId = Required(request.InstrumentId, nameof(request.InstrumentId));
            if (_tradingSettings.GetConfiguredInstruments().Any(x => x.InstrumentId == instrumentId && x.InstrumentType == InstrumentType.Forex))
                throw new NotSupportedException("Use RunForexResearchAsync (ModelOps fx-replay) for FX pattern/trade replay.");
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
                throw new InvalidOperationException("No market-data events exist in the requested dataset range.");
            }
            var shortProfile = GetShortReplayInstrument(instrumentId, strategyId);
            if (shortProfile != null) EnsureMonotonicAvailability(records);
            if (!records.Any(item => item.DataType.Equals("bar", StringComparison.OrdinalIgnoreCase))
                && (shortProfile == null || !records.Any(item => item.DataType.Equals("trade", StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("Replay requires bar events or trade ticks with an enabled short-signal profile.");
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
                FeatureVersion = _featureEngine.FeatureVersion,
                PatternVersion = GetPatternVersion(instrumentId, strategyId),
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
                if (GetShortReplayInstrument(run.InstrumentId, run.StrategyId) != null)
                    EnsureMonotonicAvailability(records);
                if (!run.PatternVersion.Equals(GetPatternVersion(run.InstrumentId, run.StrategyId), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Pattern detector configuration changed after the replay run was created; start a new run with the new version.");
                }
                if (!run.FeatureVersion.Equals(_featureEngine.FeatureVersion, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Canonical feature configuration changed after the replay run was created; start a new run with the new version.");
                }
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
                var quoteState = new ReplayQuoteState();
                var shortInstrument = GetShortReplayInstrument(run.InstrumentId, run.StrategyId);
                var shortLatest = shortInstrument == null ? null : new LatestMarketDataService(Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings));
                var shortTrigger = shortInstrument == null ? null : new ShortSignalTriggerService(
                    Microsoft.Extensions.Options.Options.Create(_shortSignalSettings),
                    Microsoft.Extensions.Options.Options.Create(_tradingSettings),
                    Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings),
                    shortLatest!, _patternDetectorFactory, _featureEngine, null,
                    NullLogger<ShortSignalTriggerService>.Instance);
                var shortIntervals = (_shortSignalSettings.Profiles ?? [])
                    .Where(profile => profile.Enabled
                        && profile.InstrumentId.Equals(run.InstrumentId, StringComparison.OrdinalIgnoreCase)
                        && profile.StrategyId.Equals(run.StrategyId, StringComparison.OrdinalIgnoreCase))
                    .Select(profile => profile.IntervalSeconds).Distinct().ToArray();
                var shortDeadlines = new SortedSet<DateTime>();
                string? latestSourceEventId = null;
                DateTime? previousEventTimeUtc = null;
                var processedThisBatch = 0;

                for (var index = 0; index < records.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var record = records[index];
                    var availableAtUtc = AvailabilityTime(record);
                    var isWarmup = index < run.ProcessedEventCount;
                    if (!isWarmup)
                    {
                        await DelayForSpeedAsync(previousEventTimeUtc, record.EventTimeUtc, run.SpeedMultiplier, cancellationToken).ConfigureAwait(false);
                    }

                    if (shortTrigger != null)
                    {
                        while (shortDeadlines.Count > 0 && shortDeadlines.Min <= availableAtUtc)
                        {
                            var deadline = shortDeadlines.Min;
                            shortDeadlines.Remove(deadline);
                            shortLatest!.AdvanceTo(deadline);
                            var shortCollection = await shortTrigger.CollectWithResearchAsync(shortInstrument!, deadline, cancellationToken).ConfigureAwait(false);
                            if (isWarmup) continue;
                            await PersistShortCollectionAsync(db, run, latestSourceEventId!, quoteState,
                                shortCollection, deadline, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    quoteState.Apply(record);
                    if (shortTrigger != null && IsHealthyTick(record))
                    {
                        // Replay is governed by the market event clock. The isolated aggregator and
                        // collector cannot publish to the live bus or reach the broker.
                        shortLatest!.Apply(new CanonicalMarketDataEvent
                        {
                            EventId = record.EventId, InstrumentId = record.InstrumentId, Symbol = record.Symbol,
                            Kind = Enum.Parse<MarketDataEventKind>(record.DataType, true),
                            EventTimeUtc = record.EventTimeUtc, ReceivedTimeUtc = availableAtUtc,
                            Source = record.Source, Sequence = record.Sequence, IsFinal = record.IsFinal,
                            Price = record.Price, Size = record.Size
                        });
                        if (record.DataType.Equals("trade", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var interval in shortIntervals)
                            {
                                var windowTicks = TimeSpan.FromSeconds(interval).Ticks;
                                var windowStartTicks = record.EventTimeUtc.Ticks - record.EventTimeUtc.Ticks % windowTicks;
                                var deadline = new DateTime(windowStartTicks, DateTimeKind.Utc)
                                    .AddSeconds(interval + _shortAggregateSettings.AllowedLatenessSeconds);
                                if (deadline > availableAtUtc) shortDeadlines.Add(deadline);
                            }
                        }
                    }

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

                        var features = _featureEngine.ComputeFeatures(quoteState.CreateFeatureInput(history, record.EventTimeUtc));
                        var batch = detector.Process(new PatternDetectionInput
                        {
                            Candles = history,
                            StrategyId = run.StrategyId,
                            FeatureVersion = run.FeatureVersion,
                            Directions = new[] { TradeDirection.Long, TradeDirection.Short }
                        });
                        if (!isWarmup)
                        {
                            if (_candidateResearchService != null)
                            {
                                var evaluationContext = new CandidateEvaluationContext(features.Regime, features.NormalizedLiquidity, features.AtrToPriceRatio);
                                await _candidateResearchService.PersistBatchAsync(batch, record.EventId, run.Id, evaluationContext, cancellationToken).ConfigureAwait(false);
                            }
                            foreach (var pattern in batch.Candidates)
                            {
                                await PersistSignalAsync(db, run, record.EventId, pattern, features, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    previousEventTimeUtc = record.EventTimeUtc;
                    latestSourceEventId = record.EventId;
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
                    if (shortTrigger != null)
                    {
                        while (shortDeadlines.Count > 0 && shortDeadlines.Min < run.ToUtc)
                        {
                            var deadline = shortDeadlines.Min;
                            shortDeadlines.Remove(deadline);
                            shortLatest!.AdvanceTo(deadline);
                            var shortCollection = await shortTrigger.CollectWithResearchAsync(shortInstrument!, deadline, cancellationToken).ConfigureAwait(false);
                            await PersistShortCollectionAsync(db, run, latestSourceEventId!, quoteState,
                                shortCollection, deadline, cancellationToken).ConfigureAwait(false);
                        }
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                    if (_candidateResearchService != null)
                    {
                        await _candidateResearchService.LabelMatureCandidatesAsync(run.ToUtc, run.Id, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
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
                foreach (var entry in db.ChangeTracker.Entries<ReplaySignalRecord>().Where(item => item.State == EntityState.Added))
                {
                    entry.State = EntityState.Detached;
                }
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
            if (run.StrategyVersion.StartsWith(ForexResearchContract.Labels, StringComparison.Ordinal))
                throw new InvalidOperationException("FX research runs are atomic offline operations; start a new fx-replay instead of changing their status.");
            if (!allowed) throw new InvalidOperationException($"Replay run in status {run.Status} cannot transition to {target}.");

            run.Status = target;
            if (speedMultiplier.HasValue) run.SpeedMultiplier = speedMultiplier.Value;
            if (target == ReplayRunStatus.Running) run.StartedAtUtc ??= ToUtc(_clock.UtcNow);
            if (target == ReplayRunStatus.Cancelled) run.CompletedAtUtc = ToUtc(_clock.UtcNow);
            Touch(run);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(run);
        }

        private async Task PersistShortCollectionAsync(
            TradingBotDbContext db,
            ReplayRunRecord run,
            string sourceEventId,
            ReplayQuoteState quoteState,
            ShortSignalCollection collection,
            DateTime availableAtUtc,
            CancellationToken cancellationToken)
        {
            if (_candidateResearchService != null)
            {
                foreach (var research in collection.ResearchBatches)
                {
                    var quote = quoteState.CreateFeatureInput(research.Candles, availableAtUtc);
                    var features = _featureEngine.ComputeFeatures(quote);
                    var context = new CandidateEvaluationContext(features.Regime,
                        features.NormalizedLiquidity, features.AtrToPriceRatio,
                        availableAtUtc, quote.Bid, quote.BidTimeUtc, quote.Ask, quote.AskTimeUtc);
                    await _candidateResearchService.PersistBatchAsync(research.Batch, sourceEventId,
                        run.Id, context, cancellationToken).ConfigureAwait(false);
                }
            }
            foreach (var selected in collection.Signals)
            {
                var features = _featureEngine.ComputeFeatures(
                    quoteState.CreateFeatureInput(selected.Candles, availableAtUtc));
                await PersistSignalAsync(db, run, sourceEventId, selected.Candidate, features,
                    cancellationToken, availableAtUtc).ConfigureAwait(false);
            }
        }

        private async Task PersistSignalAsync(
            TradingBotDbContext db,
            ReplayRunRecord run,
            string sourceEventId,
            PatternCandidate pattern,
            MarketFeatures features,
            CancellationToken cancellationToken,
            DateTime? availableAtUtc = null)
        {
            var signalId = pattern.Context.SignalId;
            if (db.ReplaySignalRecords.Local.Any(item => item.ReplayRunId == run.Id && item.SignalId == signalId)
                || await db.ReplaySignalRecords.AnyAsync(
                    item => item.ReplayRunId == run.Id && item.SignalId == signalId,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in pattern.Metadata)
            {
                metadata[item.Key] = item.Value;
            }
            if (availableAtUtc.HasValue)
                metadata[ReplayTradeSimulator.AvailableAtMetadataKey] = availableAtUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            db.ReplaySignalRecords.Add(new ReplaySignalRecord
            {
                ReplayRunId = run.Id,
                SignalId = signalId,
                SourceEventId = sourceEventId,
                PatternType = pattern.PatternType,
                Direction = pattern.Direction,
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
                new DatasetQuery(instrumentId, fromUtc, toUtc, new[] { "bar", "bid", "ask", "trade" }),
                cancellationToken).ConfigureAwait(false);
            return records
                .GroupBy(item => item.EventId, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.ReceivedTimeUtc).First())
                .OrderBy(item => item.EventTimeUtc)
                .ThenBy(item => item.ReceivedTimeUtc)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .ToArray();
        }

        private ConfiguredInstrument? GetShortReplayInstrument(string instrumentId, string strategyId)
        {
            var instrument = _tradingSettings.GetEnabledInstruments().FirstOrDefault(item =>
                item.InstrumentId.Equals(instrumentId, StringComparison.OrdinalIgnoreCase)
                && item.StrategyIds.Contains(strategyId, StringComparer.OrdinalIgnoreCase));
            if (instrument == null) return null;
            return (_shortSignalSettings.Profiles ?? []).Any(profile => profile.Enabled
                && profile.InstrumentId.Equals(instrumentId, StringComparison.OrdinalIgnoreCase)
                && profile.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase)) ? instrument : null;
        }

        /// <summary>Bounded, read-only FX feature replay; receive time is the virtual clock. Never evaluates stock patterns or fills.</summary>
        public async Task<ForexFeatureReplayResult> ReplayForexFeaturesAsync(string instrumentId, DateTime fromUtc, DateTime toUtc,
            int intervalSeconds, CancellationToken cancellationToken = default)
        {
            if (!_settings.Enabled) throw new InvalidOperationException("Deterministic replay is disabled by configuration.");
            fromUtc = ToUtc(fromUtc); toUtc = ToUtc(toUtc);
            if (fromUtc >= toUtc || toUtc - fromUtc > TimeSpan.FromDays(7)) throw new ArgumentException("FX feature replay requires a positive range of at most seven days.");
            if (!_shortAggregateSettings.IntervalsSeconds.Contains(intervalSeconds)) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            var instrument = _tradingSettings.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
            if (instrument.InstrumentType != InstrumentType.Forex) throw new ArgumentException("FX instrument required.", nameof(instrumentId));
            var verified = await _datasetStore.VerifyAsync(cancellationToken).ConfigureAwait(false);
            if (!verified.IsValid) throw new InvalidDataException("Dataset integrity verification failed.");
            var records = (await _datasetStore.ReadAsync(new DatasetQuery(instrumentId, fromUtc, toUtc, ["bid", "ask"]), cancellationToken).ConfigureAwait(false))
                .Where(x => x.ReceivedTimeUtc <= toUtc)
                .OrderBy(x => x.ReceivedTimeUtc).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
            if (records.Length == 0) throw new InvalidOperationException("No healthy as-of FX quote input exists in the requested range.");
            if (records.Any(x => !x.IsFinal || !x.CanPersist || x.QualityStatus != "Healthy"))
                throw new InvalidDataException("FX feature replay requires healthy input; repair or explicitly select a clean coverage range.");
            if (records.Any(x => x.InstrumentId != instrument.InstrumentId || x.Symbol != instrument.Symbol))
                throw new InvalidDataException("FX quote identity does not match the configured instrument.");
            if (records.Any(x => x.Source != ForexQuoteAggregator.QuoteSource))
                throw new InvalidDataException("FX feature replay requires v2 paired quote provenance; legacy quote halves cannot be inferred.");
            var latest = new LatestMarketDataService(Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings), Microsoft.Extensions.Options.Options.Create(_tradingSettings));
            var featureService = new ForexFeatureService(latest, _featureEngine, Microsoft.Extensions.Options.Options.Create(_tradingSettings), Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings));
            var frames = new List<MarketFeatures>();
            DateTime lastEnd = default;
            void Capture(DateTime now)
            {
                latest.AdvanceTo(now);
                var newest = latest.GetCompletedForexAggregates(instrumentId, intervalSeconds, now, 1);
                if (newest.Count == 0 || newest[0].WindowEndUtc <= lastEnd) return;
                foreach (var bar in latest.GetCompletedForexAggregates(instrumentId, intervalSeconds, now, _shortAggregateSettings.MaxCompletedPerInterval))
                {
                    if (bar.WindowEndUtc <= lastEnd) continue;
                    var frame = featureService.Compute(instrumentId, intervalSeconds, bar.AvailableAtUtc);
                    if (frame != null) frames.Add(frame);
                    lastEnd = bar.WindowEndUtc;
                }
            }
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Capture(record.ReceivedTimeUtc);
                latest.Apply(new CanonicalMarketDataEvent
                {
                    EventId = record.EventId, InstrumentId = record.InstrumentId, Symbol = record.Symbol,
                    Kind = record.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
                    EventTimeUtc = record.EventTimeUtc, ReceivedTimeUtc = record.ReceivedTimeUtc,
                    Source = record.Source, Price = record.Price, Size = record.Size, IsFinal = record.IsFinal
                });
            }
            Capture(toUtc); // Close only windows actually available by the requested end, not an arbitrary future flush.
            return new ForexFeatureReplayResult(ComputeInputHash(records), ForexQuoteAggregator.VersionFor(_shortAggregateSettings), frames,
                latest.GetForexDiagnostics(instrumentId));
        }

        private static DateTime AvailabilityTime(MarketDatasetRecord record) =>
            record.ReceivedTimeUtc > record.EventTimeUtc ? record.ReceivedTimeUtc : record.EventTimeUtc;

        private static void EnsureMonotonicAvailability(IReadOnlyList<MarketDatasetRecord> records)
        {
            DateTime? previous = null;
            foreach (var record in records)
            {
                var available = AvailabilityTime(record);
                if (previous.HasValue && available < previous.Value)
                    throw new InvalidDataException("Short replay input has receive-time order inconsistent with event-time order; refusing a look-ahead replay.");
                previous = available;
            }
        }

        private string GetPatternVersion(string instrumentId, string strategyId)
        {
            var instrument = GetShortReplayInstrument(instrumentId, strategyId);
            if (instrument == null) return $"{_patternDetectorFactory.DetectorVersion}+replay-signal-v2";
            var profiles = (_shortSignalSettings.Profiles ?? [])
                .Where(profile => profile.Enabled
                    && profile.InstrumentId.Equals(instrumentId, StringComparison.OrdinalIgnoreCase)
                    && profile.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(profile => profile.IntervalSeconds).ToArray();
            var canonical = JsonSerializer.Serialize(new
            {
                Profiles = profiles, Aggregates = new { _shortAggregateSettings.IntervalsSeconds,
                    _shortAggregateSettings.AllowedLatenessSeconds, _shortAggregateSettings.MaxCompletedPerInterval },
                instrument.AllowedDirections
            }, JsonOptions);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            return $"{_patternDetectorFactory.DetectorVersion}+short-replay-v3-{hash}";
        }

        private static bool IsHealthyTick(MarketDatasetRecord record) =>
            (record.DataType.Equals("bid", StringComparison.OrdinalIgnoreCase)
                || record.DataType.Equals("ask", StringComparison.OrdinalIgnoreCase)
                || record.DataType.Equals("trade", StringComparison.OrdinalIgnoreCase))
            && record.IsFinal && record.CanTriggerTrading && record.Price is > 0m
            && record.QualityStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase);

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

        internal static string ComputeInputHash(IReadOnlyList<MarketDatasetRecord> records)
        {
            var canonical = new StringBuilder(records.Count * 160);
            foreach (var item in records)
            {
                canonical.Append(item.SchemaVersion).Append('|')
                    .Append(item.EventId).Append('|')
                    .Append(item.InstrumentId).Append('|')
                    .Append(item.Symbol).Append('|')
                    .Append(item.DataType).Append('|')
                    .Append(item.EventTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Source).Append('|')
                    .Append(item.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
                    .Append(item.IsFinal).Append('|').Append(item.Timeframe).Append('|')
                    .Append(Decimal(item.Price)).Append('|').Append(Decimal(item.Size)).Append('|')
                    .Append(Decimal(item.Open)).Append('|').Append(Decimal(item.High)).Append('|')
                    .Append(Decimal(item.Low)).Append('|').Append(Decimal(item.Close)).Append('|')
                    .Append(Decimal(item.Volume)).Append('|').Append(item.QualityStatus).Append('|')
                    .Append(item.QualityReason).Append('|').Append(item.CanPersist).Append('|')
                    .Append(item.CanTriggerTrading).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        }

        internal static async Task<string> ComputeOutputHashAsync(
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
                    .Append(item.Direction).Append('|')
                    .Append(item.Symbol).Append('|').Append(item.Timeframe).Append('|')
                    .Append(item.DetectedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Confidence.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.RelevantPriceLevelsJson).Append('|').Append(item.MetadataJson).Append('|')
                    .Append(item.FeaturesJson).Append('\n');
            }
            var candidates = await db.ResearchCandidateRecords.AsNoTracking()
                .Where(item => item.ReplayRunId == replayRunId)
                .OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.CandidateKey)
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in candidates)
            {
                canonical.Append(item.CandidateKey).Append('|').Append(item.SourceEventId).Append('|')
                    .Append(item.EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Outcome).Append('|').Append(item.DecisionStage).Append('|')
                    .Append(item.ReferencePrice.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Confidence.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.MarketRegime).Append('|')
                    .Append(Decimal(item.NormalizedLiquidity)).Append('|')
                    .Append(Decimal(item.NormalizedVolatility)).Append('|')
                    .Append(item.HardConditionsJson).Append('|').Append(item.ScoreComponentsJson).Append('|')
                    .Append(item.ReasonsJson).Append('|').Append(item.LabelVersion).Append('\n');
            }
            var labels = await db.CandidateLabelRecords.AsNoTracking()
                .Where(item => item.ResearchCandidate.ReplayRunId == replayRunId)
                .OrderBy(item => item.ResearchCandidate.CandidateKey).ThenBy(item => item.HorizonSeconds)
                .Select(item => new { item.ResearchCandidate.CandidateKey, Label = item })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in labels)
            {
                var label = item.Label;
                canonical.Append(item.CandidateKey).Append('|').Append(label.HorizonSeconds).Append('|')
                    .Append(label.WindowStartUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(label.WindowEndUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(label.EntryPrice.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(label.Status).Append('|').Append(label.TargetStopOutcome).Append('|')
                    .Append(Decimal(label.ExitPrice)).Append('|').Append(Decimal(label.MaximumFavorableExcursionBps)).Append('|')
                    .Append(Decimal(label.HorizonExitPrice)).Append('|').Append(Decimal(label.HorizonGrossReturnBps)).Append('|')
                    .Append(Decimal(label.MaximumAdverseExcursionBps)).Append('|').Append(Decimal(label.GrossReturnBps)).Append('|')
                    .Append(Decimal(label.EstimatedCostBps)).Append('|').Append(Decimal(label.NetReturnBps)).Append('|')
                    .Append(label.ReasonsJson).Append('|').Append(label.LabelVersion).Append('|')
                    .Append(label.InputSha256).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        }

        private sealed class ReplayQuoteState
        {
            private decimal? _bid;
            private DateTime? _bidTimeUtc;
            private decimal? _ask;
            private DateTime? _askTimeUtc;
            private decimal? _lastTrade;
            private DateTime? _lastTradeTimeUtc;

            public void Apply(MarketDatasetRecord record)
            {
                if (!record.IsFinal || !record.CanTriggerTrading || record.Price is not > 0m
                    || !Enum.TryParse<MarketDataQualityStatus>(record.QualityStatus, true, out var quality)
                    || quality != MarketDataQualityStatus.Healthy)
                {
                    return;
                }

                switch (record.DataType.Trim().ToLowerInvariant())
                {
                    case "bid":
                        _bid = record.Price;
                        _bidTimeUtc = record.EventTimeUtc;
                        break;
                    case "ask":
                        _ask = record.Price;
                        _askTimeUtc = record.EventTimeUtc;
                        break;
                    case "trade":
                        _lastTrade = record.Price;
                        _lastTradeTimeUtc = record.EventTimeUtc;
                        break;
                }
            }

            public CanonicalFeatureInput CreateFeatureInput(IReadOnlyList<DomainCandle> candles, DateTime asOfUtc) => new()
            {
                Candles = candles,
                AsOfUtc = asOfUtc,
                Bid = _bid,
                BidTimeUtc = _bidTimeUtc,
                Ask = _ask,
                AskTimeUtc = _askTimeUtc,
                LastTrade = _lastTrade,
                LastTradeTimeUtc = _lastTradeTimeUtc
            };
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
            item.Direction,
            item.Symbol, item.Timeframe, item.DetectedAtUtc, item.Confidence,
            item.RelevantPriceLevelsJson, item.MetadataJson, item.FeaturesJson);
    }
}
