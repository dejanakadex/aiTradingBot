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

namespace TradingBot.Infrastructure.Services
{
    public sealed class CandidateResearchService : ICandidateResearchService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IMarketDatasetStore _datasetStore;
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IClock _clock;
        private readonly CandidateLabelingSettings _settings;
        private readonly CandidateLabelCalculator _calculator;
        private readonly ILogger<CandidateResearchService> _logger;

        public CandidateResearchService(
            IMarketDatasetStore datasetStore,
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IClock clock,
            IOptions<CandidateLabelingSettings> settings,
            ILogger<CandidateResearchService> logger)
        {
            _datasetStore = datasetStore ?? throw new ArgumentNullException(nameof(datasetStore));
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _calculator = new CandidateLabelCalculator(_settings);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            LabelVersion = CandidateLabelVersion.Create(_settings);
        }

        public string LabelVersion { get; }

        public async Task<int> PersistBatchAsync(
            PatternDetectionBatch batch,
            string sourceEventId,
            Guid? replayRunId = null,
            CandidateEvaluationContext? evaluationContext = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (string.IsNullOrWhiteSpace(sourceEventId)) throw new ArgumentException("Source event id is required.", nameof(sourceEventId));
            if (batch.Evaluations.Count == 0) return 0;

            var prefix = ScopePrefix(replayRunId);
            var emitted = batch.Candidates.Select(item => item.PatternKey).ToHashSet(StringComparer.Ordinal);
            var recordKeys = batch.Evaluations.Select(item => $"{prefix}|{item.EvaluationKey}").Distinct(StringComparer.Ordinal).ToArray();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var existing = (await db.ResearchCandidateRecords.AsNoTracking()
                    .Where(item => recordKeys.Contains(item.RecordKey))
                    .Select(item => item.RecordKey)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.Ordinal);
            var now = ToUtc(_clock.UtcNow);
            var added = 0;

            foreach (var evaluation in batch.Evaluations)
            {
                var recordKey = $"{prefix}|{evaluation.EvaluationKey}";
                if (!existing.Add(recordKey)) continue;
                var candidate = evaluation.Candidate;
                var outcome = candidate == null
                    ? ResearchCandidateOutcome.Rejected
                    : emitted.Contains(evaluation.EvaluationKey)
                        ? ResearchCandidateOutcome.Accepted
                        : ResearchCandidateOutcome.Blocked;
                var stage = outcome switch
                {
                    ResearchCandidateOutcome.Rejected => "PatternHardConditions",
                    ResearchCandidateOutcome.Blocked => "PatternDeduplication",
                    _ => "PatternDetected"
                };
                var reasons = evaluation.Reasons.Count > 0
                    ? evaluation.Reasons
                    : outcome == ResearchCandidateOutcome.Blocked
                        ? new[] { "Pattern passed deterministic conditions but was suppressed as a duplicate." }
                        : Array.Empty<string>();
                var record = new ResearchCandidateRecord
                {
                    RecordKey = recordKey,
                    CandidateKey = evaluation.EvaluationKey,
                    ReplayRunId = replayRunId,
                    SignalId = candidate?.Context.SignalId,
                    SourceEventId = sourceEventId.Trim(),
                    InstrumentId = evaluation.InstrumentId,
                    Symbol = evaluation.Symbol,
                    StrategyId = evaluation.StrategyId,
                    PatternType = evaluation.PatternType,
                    Direction = evaluation.Direction,
                    Timeframe = evaluation.Timeframe,
                    EvaluatedAtUtc = ToUtc(evaluation.EvaluatedAtUtc),
                    ReferencePrice = evaluation.ReferencePrice,
                    Confidence = candidate?.Confidence ?? evaluation.FinalScore,
                    Outcome = outcome,
                    DecisionStage = stage,
                    FeatureVersion = evaluation.FeatureVersion,
                    PatternVersion = evaluation.PatternVersion,
                    LabelVersion = LabelVersion,
                    MarketRegime = evaluationContext?.MarketRegime.ToString() ?? MarketRegime.Unknown.ToString(),
                    NormalizedLiquidity = evaluationContext?.NormalizedLiquidity,
                    NormalizedVolatility = evaluationContext?.NormalizedVolatility,
                    HardConditionsJson = JsonSerializer.Serialize(evaluation.HardConditions, JsonOptions),
                    ScoreComponentsJson = JsonSerializer.Serialize(evaluation.ScoreComponents, JsonOptions),
                    ReasonsJson = JsonSerializer.Serialize(reasons, JsonOptions),
                    MetadataJson = JsonSerializer.Serialize(candidate?.Metadata ?? new Dictionary<string, string>(), JsonOptions),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                foreach (var horizon in _settings.HorizonsSeconds.OrderBy(value => value))
                {
                    record.Labels.Add(new CandidateLabelRecord
                    {
                        HorizonSeconds = horizon,
                        Status = CandidateLabelStatus.Pending,
                        TargetStopOutcome = TargetStopOutcome.Neither,
                        WindowStartUtc = record.EvaluatedAtUtc,
                        WindowEndUtc = record.EvaluatedAtUtc.AddSeconds(horizon),
                        EntryPrice = record.ReferencePrice,
                        LabelVersion = LabelVersion,
                        ReasonsJson = JsonSerializer.Serialize(new[] { "Waiting for the complete as-of label horizon." }, JsonOptions)
                    });
                }
                db.ResearchCandidateRecords.Add(record);
                added++;
            }

            if (added > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return added;
        }

        public async Task MarkBlockedAsync(
            string candidateKey,
            string stage,
            IReadOnlyCollection<string> reasons,
            Guid? replayRunId = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(candidateKey)) throw new ArgumentException("Candidate key is required.", nameof(candidateKey));
            if (string.IsNullOrWhiteSpace(stage)) throw new ArgumentException("Decision stage is required.", nameof(stage));
            var recordKey = $"{ScopePrefix(replayRunId)}|{candidateKey.Trim().ToUpperInvariant()}";
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.ResearchCandidateRecords.SingleOrDefaultAsync(item => item.RecordKey == recordKey, cancellationToken).ConfigureAwait(false);
            if (record == null)
            {
                _logger.LogWarning("Cannot mark unknown research candidate {CandidateKey} as blocked at {Stage}.", candidateKey, stage);
                return;
            }
            if (record.Outcome == ResearchCandidateOutcome.Rejected) return;
            record.Outcome = ResearchCandidateOutcome.Blocked;
            record.DecisionStage = stage.Trim();
            record.ReasonsJson = JsonSerializer.Serialize(reasons ?? Array.Empty<string>(), JsonOptions);
            record.UpdatedAtUtc = ToUtc(_clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<int> LabelMatureCandidatesAsync(
            DateTime asOfUtc,
            Guid? replayRunId = null,
            int? maximumCandidates = null,
            CancellationToken cancellationToken = default)
        {
            var asOf = ToUtc(asOfUtc);
            var maturityCutoff = replayRunId.HasValue
                ? asOf
                : asOf.AddSeconds(-_settings.DataAvailabilityGraceSeconds);
            var limit = maximumCandidates ?? _settings.BatchSize;
            if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.ResearchCandidateRecords
                .Include(item => item.Labels)
                .Where(item => item.Labels.Any(label => label.Status == CandidateLabelStatus.Pending && label.WindowEndUtc <= maturityCutoff));
            if (replayRunId.HasValue) query = query.Where(item => item.ReplayRunId == replayRunId);
            else query = query.Where(item => item.ReplayRunId == null);
            var candidates = await query.OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.Id)
                .Take(limit).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var calculated = 0;

            foreach (var candidate in candidates)
            {
                var matured = candidate.Labels
                    .Where(label => label.Status == CandidateLabelStatus.Pending && label.WindowEndUtc <= maturityCutoff)
                    .OrderBy(label => label.HorizonSeconds).ToArray();
                if (matured.Length == 0) continue;
                var records = await _datasetStore.ReadAsync(new DatasetQuery(
                    candidate.InstrumentId,
                    candidate.EvaluatedAtUtc,
                    matured[^1].WindowEndUtc.AddTicks(1),
                    new[] { "bar", "bid", "ask", "trade" }), cancellationToken).ConfigureAwait(false);
                var prices = CreatePriceObservations(records);
                var spreads = CreateSpreadObservations(records);
                foreach (var label in matured)
                {
                    var result = _calculator.Calculate(candidate.Direction, candidate.ReferencePrice,
                        candidate.EvaluatedAtUtc, label.WindowEndUtc, asOf, prices, spreads);
                    Apply(label, result, asOf);
                    calculated++;
                }
                candidate.UpdatedAtUtc = ToUtc(_clock.UtcNow);
            }

            if (calculated > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return calculated;
        }

        public async Task<IReadOnlyList<ResearchCandidateSnapshot>> GetCandidatesAsync(
            string? instrumentId = null,
            ResearchCandidateOutcome? outcome = null,
            int count = 100,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.ResearchCandidateRecords.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(instrumentId)) query = query.Where(item => item.InstrumentId == instrumentId.Trim());
            if (outcome.HasValue) query = query.Where(item => item.Outcome == outcome.Value);
            var records = await query.OrderByDescending(item => item.EvaluatedAtUtc).Take(Math.Clamp(count, 1, 1000))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<IReadOnlyList<CandidateLabelSnapshot>> GetLabelsAsync(long researchCandidateId, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.CandidateLabelRecords.AsNoTracking().Where(item => item.ResearchCandidateId == researchCandidateId)
                .OrderBy(item => item.HorizonSeconds).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        private void Apply(CandidateLabelRecord label, CandidateLabelCalculation result, DateTime calculatedAtUtc)
        {
            label.Status = result.Status;
            label.TargetStopOutcome = result.TargetStopOutcome;
            label.ObservationCount = result.ObservationCount;
            label.ExitPrice = result.ExitPrice;
            label.MaximumFavorableExcursionBps = result.MaximumFavorableExcursionBps;
            label.MaximumAdverseExcursionBps = result.MaximumAdverseExcursionBps;
            label.GrossReturnBps = result.GrossReturnBps;
            label.EstimatedCostBps = result.EstimatedCostBps;
            label.NetReturnBps = result.NetReturnBps;
            label.ObservedSpreadBps = result.ObservedSpreadBps;
            label.FirstTargetEventId = result.FirstTargetEventId;
            label.FirstStopEventId = result.FirstStopEventId;
            label.MaximumEventTimeUtc = result.MaximumEventTimeUtc;
            label.ReasonsJson = JsonSerializer.Serialize(result.Reasons, JsonOptions);
            label.LabelVersion = LabelVersion;
            label.CalculatedAtUtc = calculatedAtUtc;
        }

        private static IReadOnlyList<CandidatePriceObservation> CreatePriceObservations(IEnumerable<MarketDatasetRecord> records) => records
            .Where(item => item.CanPersist)
            .Select(item => item.DataType.Trim().ToLowerInvariant() switch
            {
                "trade" when item.Price is > 0m => new CandidatePriceObservation(item.EventTimeUtc, item.Price.Value, item.Price.Value, item.Price.Value, item.EventId),
                "bar" when item.High is > 0m && item.Low is > 0m && item.Close is > 0m => new CandidatePriceObservation(item.EventTimeUtc, item.High.Value, item.Low.Value, item.Close.Value, item.EventId, true),
                _ => null
            })
            .Where(item => item != null).Select(item => item!).OrderBy(item => item.EventTimeUtc).ThenBy(item => item.SourceEventId, StringComparer.Ordinal).ToArray();

        private static IReadOnlyList<CandidateSpreadObservation> CreateSpreadObservations(IEnumerable<MarketDatasetRecord> records)
        {
            decimal? bid = null;
            decimal? ask = null;
            var result = new List<CandidateSpreadObservation>();
            foreach (var item in records.Where(item => item.CanPersist).OrderBy(item => item.EventTimeUtc).ThenBy(item => item.ReceivedTimeUtc).ThenBy(item => item.EventId, StringComparer.Ordinal))
            {
                if (item.DataType.Equals("bid", StringComparison.OrdinalIgnoreCase) && item.Price is > 0m) bid = item.Price;
                if (item.DataType.Equals("ask", StringComparison.OrdinalIgnoreCase) && item.Price is > 0m) ask = item.Price;
                if (bid.HasValue && ask >= bid) result.Add(new CandidateSpreadObservation(item.EventTimeUtc, bid.Value, ask.Value));
            }
            return result;
        }

        private static string ScopePrefix(Guid? replayRunId) => replayRunId?.ToString("N").ToUpperInvariant() ?? "LIVE";
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private static ResearchCandidateSnapshot ToSnapshot(ResearchCandidateRecord item) => new(
            item.Id, item.RecordKey, item.CandidateKey, item.ReplayRunId, item.SignalId, item.SourceEventId,
            item.InstrumentId, item.Symbol, item.StrategyId, item.PatternType, item.Direction, item.Timeframe,
            item.EvaluatedAtUtc, item.ReferencePrice, item.Confidence, item.Outcome, item.DecisionStage,
            item.FeatureVersion, item.PatternVersion, item.LabelVersion, item.MarketRegime,
            item.NormalizedLiquidity, item.NormalizedVolatility, item.ReasonsJson, item.CreatedAtUtc, item.UpdatedAtUtc);

        private static CandidateLabelSnapshot ToSnapshot(CandidateLabelRecord item) => new(
            item.Id, item.ResearchCandidateId, item.HorizonSeconds, item.Status, item.TargetStopOutcome,
            item.WindowStartUtc, item.WindowEndUtc, item.ObservationCount, item.EntryPrice, item.ExitPrice,
            item.MaximumFavorableExcursionBps, item.MaximumAdverseExcursionBps, item.GrossReturnBps,
            item.EstimatedCostBps, item.NetReturnBps, item.ObservedSpreadBps, item.MaximumEventTimeUtc,
            item.ReasonsJson, item.LabelVersion, item.CalculatedAtUtc);
    }
}
