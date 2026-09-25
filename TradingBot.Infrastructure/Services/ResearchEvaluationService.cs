using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class ResearchEvaluationService : IResearchEvaluationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IClock _clock;
        private readonly ResearchEvaluationSettings _settings;
        private readonly ResearchEvaluationCalculator _calculator;

        public ResearchEvaluationService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IClock clock,
            IOptions<ResearchEvaluationSettings> settings)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _calculator = new ResearchEvaluationCalculator(_settings);
            EvaluationVersion = ResearchEvaluationVersion.Create(_settings);
        }

        public string EvaluationVersion { get; }

        public async Task<ResearchEvaluationRunSnapshot> RunAsync(
            ResearchEvaluationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.HorizonSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(request.HorizonSeconds));
            var requestedFrom = request.FromUtc.HasValue ? ToUtc(request.FromUtc.Value) : (DateTime?)null;
            var requestedTo = request.ToUtc.HasValue ? ToUtc(request.ToUtc.Value) : (DateTime?)null;
            if (requestedFrom.HasValue && requestedTo.HasValue && requestedFrom >= requestedTo)
            {
                throw new ArgumentException("Evaluation FromUtc must be earlier than ToUtc.", nameof(request));
            }

            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var query = db.CandidateLabelRecords.AsNoTracking()
                .Where(label => label.HorizonSeconds == request.HorizonSeconds)
                .Where(label => label.Status == CandidateLabelStatus.Complete || label.Status == CandidateLabelStatus.Ambiguous)
                .Where(label => label.GrossReturnBps.HasValue && label.EstimatedCostBps.HasValue && label.NetReturnBps.HasValue
                    && label.MaximumFavorableExcursionBps.HasValue && label.MaximumAdverseExcursionBps.HasValue);
            query = request.ReplayRunId.HasValue
                ? query.Where(label => label.ResearchCandidate.ReplayRunId == request.ReplayRunId)
                : query.Where(label => label.ResearchCandidate.ReplayRunId == null);
            if (!string.IsNullOrWhiteSpace(request.InstrumentId))
            {
                var instrument = request.InstrumentId.Trim();
                query = query.Where(label => label.ResearchCandidate.InstrumentId == instrument);
            }
            if (!string.IsNullOrWhiteSpace(request.StrategyId))
            {
                var strategy = request.StrategyId.Trim();
                query = query.Where(label => label.ResearchCandidate.StrategyId == strategy);
            }
            if (requestedFrom.HasValue) query = query.Where(label => label.ResearchCandidate.EvaluatedAtUtc >= requestedFrom.Value);
            if (requestedTo.HasValue) query = query.Where(label => label.WindowEndUtc <= requestedTo.Value);

            var rows = await query.OrderBy(label => label.ResearchCandidate.EvaluatedAtUtc)
                .ThenBy(label => label.ResearchCandidate.CandidateKey)
                .Take(_settings.MaximumCandidates + 1)
                .Select(label => new ResearchEvaluationObservation(
                    label.ResearchCandidateId,
                    label.ResearchCandidate.CandidateKey,
                    label.ResearchCandidate.InstrumentId,
                    label.ResearchCandidate.StrategyId,
                    label.ResearchCandidate.PatternType,
                    label.ResearchCandidate.Direction,
                    label.ResearchCandidate.Timeframe,
                    label.ResearchCandidate.EvaluatedAtUtc,
                    label.WindowEndUtc,
                    label.ResearchCandidate.Confidence,
                    label.ResearchCandidate.Outcome,
                    label.ResearchCandidate.MarketRegime,
                    label.ResearchCandidate.NormalizedLiquidity,
                    label.GrossReturnBps!.Value,
                    label.EstimatedCostBps!.Value,
                    label.NetReturnBps!.Value,
                    label.MaximumFavorableExcursionBps!.Value,
                    label.MaximumAdverseExcursionBps!.Value,
                    label.TargetStopOutcome,
                    label.ResearchCandidate.FeatureVersion,
                    label.ResearchCandidate.PatternVersion,
                    label.LabelVersion))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Length > _settings.MaximumCandidates)
            {
                throw new InvalidOperationException($"Evaluation input exceeds the configured maximum of {_settings.MaximumCandidates} candidates; narrow the range or filter.");
            }
            if (rows.Length == 0) throw new InvalidOperationException("No completed candidate labels match the evaluation request.");

            var from = requestedFrom ?? rows.Min(item => item.EvaluatedAtUtc);
            var to = requestedTo ?? rows.Max(item => item.LabelWindowEndUtc);
            var calculation = _calculator.Calculate(rows, from, to);
            var inputHash = ComputeInputHash(rows, request, from, to);
            var runKey = Hash($"{EvaluationVersion}|{request.HorizonSeconds}|{request.ReplayRunId:N}|{request.InstrumentId?.Trim()}|{request.StrategyId?.Trim()}|{from:O}|{to:O}|{inputHash}");
            var existing = await db.ResearchEvaluationRunRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RunKey == runKey, cancellationToken).ConfigureAwait(false);
            if (existing != null) return ToSnapshot(existing);

            var now = ToUtc(_clock.UtcNow);
            var record = new ResearchEvaluationRunRecord
            {
                Id = Guid.NewGuid(),
                RunKey = runKey,
                ReplayRunId = request.ReplayRunId,
                InstrumentId = request.InstrumentId?.Trim() ?? string.Empty,
                StrategyId = request.StrategyId?.Trim() ?? string.Empty,
                HorizonSeconds = request.HorizonSeconds,
                FromUtc = calculation.FromUtc,
                ToUtc = calculation.ToUtc,
                HoldoutStartUtc = calculation.HoldoutStartUtc,
                SourceCandidateCount = calculation.SourceCandidateCount,
                EligibleCandidateCount = calculation.EligibleCandidateCount,
                WalkForwardFoldCount = calculation.Folds.Count(item => item.SelectedConfidenceThreshold.HasValue),
                SelectedConfidenceThreshold = calculation.SelectedConfidenceThreshold,
                SelectionReason = calculation.SelectionReason,
                EvaluationVersion = EvaluationVersion,
                FeatureVersion = calculation.FeatureVersion,
                PatternVersion = calculation.PatternVersion,
                LabelVersion = calculation.LabelVersion,
                InputSha256 = inputHash,
                PreHoldoutMetricsJson = Serialize(calculation.PreHoldoutMetrics),
                WalkForwardMetricsJson = Serialize(calculation.WalkForwardMetrics),
                HoldoutMetricsJson = Serialize(calculation.HoldoutMetrics),
                PreHoldoutThresholdsJson = Serialize(calculation.PreHoldoutThresholds),
                FoldsJson = Serialize(calculation.Folds),
                CostSensitivityJson = Serialize(calculation.CostSensitivity),
                SegmentsJson = Serialize(calculation.Segments),
                CreatedAtUtc = now,
                CompletedAtUtc = now
            };
            record.OutputSha256 = ComputeOutputHash(record);
            db.ResearchEvaluationRunRecords.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                db.Entry(record).State = EntityState.Detached;
                var concurrent = await db.ResearchEvaluationRunRecords.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.RunKey == runKey, cancellationToken).ConfigureAwait(false);
                if (concurrent != null) return ToSnapshot(concurrent);
                throw;
            }
            return ToSnapshot(record);
        }

        public async Task<IReadOnlyList<ResearchEvaluationRunSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.ResearchEvaluationRunRecords.AsNoTracking()
                .OrderByDescending(item => item.CompletedAtUtc).Take(Math.Clamp(count, 1, 500))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<ResearchEvaluationRunSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.ResearchEvaluationRunRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
            return record == null ? null : ToSnapshot(record);
        }

        private static string ComputeInputHash(
            IEnumerable<ResearchEvaluationObservation> rows,
            ResearchEvaluationRequest request,
            DateTime fromUtc,
            DateTime toUtc)
        {
            var canonical = new StringBuilder();
            canonical.Append(request.HorizonSeconds).Append('|').Append(request.ReplayRunId).Append('|')
                .Append(request.InstrumentId?.Trim()).Append('|').Append(request.StrategyId?.Trim()).Append('|')
                .Append(fromUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(toUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var item in rows.OrderBy(item => item.EvaluatedAtUtc).ThenBy(item => item.CandidateKey, StringComparer.Ordinal))
            {
                canonical.Append(item.CandidateKey).Append('|').Append(item.EvaluatedAtUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.LabelWindowEndUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Confidence.ToString(CultureInfo.InvariantCulture)).Append('|').Append(item.Outcome).Append('|')
                    .Append(item.MarketRegime).Append('|').Append(Decimal(item.NormalizedLiquidity)).Append('|')
                    .Append(item.GrossReturnBps.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.EstimatedCostBps.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.NetReturnBps.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.MaximumFavorableExcursionBps.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.MaximumAdverseExcursionBps.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.TargetStopOutcome).Append('|').Append(item.FeatureVersion).Append('|')
                    .Append(item.PatternVersion).Append('|').Append(item.LabelVersion).Append('\n');
            }
            return Hash(canonical.ToString());
        }

        private static string ComputeOutputHash(ResearchEvaluationRunRecord item) => Hash(
            $"{item.RunKey}|{item.SelectedConfidenceThreshold.ToString(CultureInfo.InvariantCulture)}|{item.PreHoldoutMetricsJson}|{item.WalkForwardMetricsJson}|{item.HoldoutMetricsJson}|{item.PreHoldoutThresholdsJson}|{item.FoldsJson}|{item.CostSensitivityJson}|{item.SegmentsJson}");

        private static ResearchEvaluationRunSnapshot ToSnapshot(ResearchEvaluationRunRecord item) => new(
            item.Id, item.RunKey, item.ReplayRunId, item.InstrumentId, item.StrategyId, item.HorizonSeconds,
            item.FromUtc, item.ToUtc, item.HoldoutStartUtc, item.SourceCandidateCount, item.EligibleCandidateCount,
            item.WalkForwardFoldCount, item.SelectedConfidenceThreshold, item.SelectionReason, item.EvaluationVersion,
            item.FeatureVersion, item.PatternVersion, item.LabelVersion, item.InputSha256, item.OutputSha256,
            Deserialize<EvaluationMetrics>(item.PreHoldoutMetricsJson),
            Deserialize<EvaluationMetrics>(item.WalkForwardMetricsJson),
            Deserialize<EvaluationMetrics>(item.HoldoutMetricsJson),
            Deserialize<ThresholdEvaluation[]>(item.PreHoldoutThresholdsJson),
            Deserialize<WalkForwardFoldResult[]>(item.FoldsJson),
            Deserialize<CostSensitivityResult[]>(item.CostSensitivityJson),
            Deserialize<EvaluationSegmentResult[]>(item.SegmentsJson),
            item.CreatedAtUtc, item.CompletedAtUtc);

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
        private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions)
            ?? throw new InvalidDataException($"Persisted evaluation payload '{typeof(T).Name}' is invalid.");
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        private static string Decimal(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
