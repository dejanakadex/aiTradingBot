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
    public sealed class ResearchCalibrationService : IResearchCalibrationService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly IResearchEvaluationService _evaluationService;
        private readonly IClock _clock;
        private readonly ResearchCalibrationSettings _settings;
        private readonly ResearchCalibrationCalculator _calculator;

        public ResearchCalibrationService(
            IDbContextFactory<TradingBotDbContext> dbFactory,
            IResearchEvaluationService evaluationService,
            IClock clock,
            IOptions<ResearchCalibrationSettings> settings)
        {
            _dbFactory = dbFactory ?? throw new ArgumentNullException(nameof(dbFactory));
            _evaluationService = evaluationService ?? throw new ArgumentNullException(nameof(evaluationService));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
            _calculator = new ResearchCalibrationCalculator(_settings);
            CalibrationVersion = ResearchCalibrationVersion.Create(_settings);
        }

        public string CalibrationVersion { get; }

        public async Task<ResearchCalibrationProfileSnapshot> GenerateAsync(
            ResearchCalibrationRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var evaluation = await _evaluationService.GetAsync(request.EvaluationRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Research evaluation '{request.EvaluationRunId}' was not found.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await LoadObservationsAsync(db, evaluation, cancellationToken).ConfigureAwait(false);
            var evaluationInputHash = ComputeEvaluationInputHash(rows, evaluation);
            if (!string.Equals(evaluation.InputSha256, evaluationInputHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Evaluation input changed after the immutable evaluation run; create a new evaluation before calibrating.");
            }
            ValidatePipelineVersions(rows, evaluation);
            var inputHash = Hash($"{evaluation.Id:N}|{evaluation.OutputSha256}|{evaluationInputHash}|{CalibrationVersion}");
            var profileKey = Hash($"{evaluation.Id:N}|{inputHash}");
            var existing = await db.ResearchCalibrationProfileRecords.AsNoTracking()
                .Include(item => item.ApprovalHistory)
                .SingleOrDefaultAsync(item => item.ProfileKey == profileKey, cancellationToken).ConfigureAwait(false);
            if (existing != null) return ToSnapshot(existing);

            var calculation = _calculator.Calculate(rows, evaluation);
            var currentVersion = await db.ResearchCalibrationProfileRecords
                .Where(item => item.InstrumentId == evaluation.InstrumentId
                    && item.StrategyId == evaluation.StrategyId
                    && item.HorizonSeconds == evaluation.HorizonSeconds)
                .Select(item => (int?)item.ModelVersion)
                .MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            var record = new ResearchCalibrationProfileRecord
            {
                Id = Guid.NewGuid(),
                ProfileKey = profileKey,
                ModelVersion = currentVersion + 1,
                Revision = 0,
                Status = ResearchCalibrationStatus.Draft,
                EvaluationRunId = evaluation.Id,
                ReplayRunId = evaluation.ReplayRunId,
                InstrumentId = evaluation.InstrumentId,
                StrategyId = evaluation.StrategyId,
                HorizonSeconds = evaluation.HorizonSeconds,
                CalibrationVersion = CalibrationVersion,
                EvaluationVersion = evaluation.EvaluationVersion,
                FeatureVersion = evaluation.FeatureVersion,
                PatternVersion = evaluation.PatternVersion,
                LabelVersion = evaluation.LabelVersion,
                StableConfidenceThreshold = calculation.StableConfidenceThreshold,
                MinimumCalibratedProbability = calculation.MinimumCalibratedProbability,
                AverageWinBps = calculation.AverageWinBps,
                AverageLossBps = calculation.AverageLossBps,
                AverageEstimatedCostBps = calculation.AverageEstimatedCostBps,
                ThresholdStabilityJson = Serialize(calculation.ThresholdStability),
                CalibrationPointsJson = Serialize(calculation.CalibrationPoints),
                WalkForwardRawMetricsJson = Serialize(calculation.WalkForwardRawMetrics),
                WalkForwardCalibratedMetricsJson = Serialize(calculation.WalkForwardCalibratedMetrics),
                HoldoutRawMetricsJson = Serialize(calculation.HoldoutRawMetrics),
                HoldoutCalibratedMetricsJson = Serialize(calculation.HoldoutCalibratedMetrics),
                BaselineComparisonJson = Serialize(calculation.BaselineComparison),
                InputSha256 = inputHash,
                CreatedAtUtc = ToUtc(_clock.UtcNow)
            };
            record.OutputSha256 = ComputeOutputHash(record);
            db.ResearchCalibrationProfileRecords.Add(record);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                db.Entry(record).State = EntityState.Detached;
                var concurrent = await db.ResearchCalibrationProfileRecords.AsNoTracking()
                    .Include(item => item.ApprovalHistory)
                    .SingleOrDefaultAsync(item => item.ProfileKey == profileKey, cancellationToken).ConfigureAwait(false);
                if (concurrent != null) return ToSnapshot(concurrent);
                throw;
            }
            return ToSnapshot(record);
        }

        public async Task<IReadOnlyList<ResearchCalibrationProfileSnapshot>> GetAllAsync(
            int count = 50,
            CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.ResearchCalibrationProfileRecords.AsNoTracking()
                .Include(item => item.ApprovalHistory)
                .OrderByDescending(item => item.CreatedAtUtc)
                .Take(Math.Clamp(count, 1, 500))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        public async Task<ResearchCalibrationProfileSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.ResearchCalibrationProfileRecords.AsNoTracking()
                .Include(item => item.ApprovalHistory)
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false);
            return record == null ? null : ToSnapshot(record);
        }

        public async Task<ResearchCalibrationProfileSnapshot> DecideAsync(
            Guid id,
            ResearchCalibrationDecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var reviewer = request.Reviewer?.Trim() ?? string.Empty;
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(reviewer)) throw new ArgumentException("A reviewer identity is required.", nameof(request));
            if (reason.Length < _settings.MinimumApprovalReasonLength)
            {
                throw new ArgumentException($"Decision reason must contain at least {_settings.MinimumApprovalReasonLength} characters.", nameof(request));
            }
            if (!Enum.IsDefined(request.Decision)) throw new ArgumentOutOfRangeException(nameof(request), "Unknown calibration decision.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.ResearchCalibrationProfileRecords
                .Include(item => item.ApprovalHistory)
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Research calibration profile '{id}' was not found.");
            if (!string.Equals(record.OutputSha256, request.ExpectedOutputSha256?.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Calibration output hash does not match the reviewed artifact.");
            }
            if (!string.Equals(record.OutputSha256, ComputeOutputHash(record), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Calibration profile failed its output hash verification.");
            }
            if (record.Status != ResearchCalibrationStatus.Draft)
            {
                throw new InvalidOperationException($"Only a Draft calibration can receive a decision; current status is {record.Status}.");
            }

            var now = ToUtc(_clock.UtcNow);
            if (request.Decision == ResearchCalibrationDecision.Approve)
            {
                var comparison = Deserialize<CalibrationBaselineComparison>(record.BaselineComparisonJson);
                if (!comparison.PassesApprovalGate)
                {
                    throw new InvalidOperationException($"Calibration is not approval-ready: {string.Join(" ", comparison.Reasons)}");
                }
                var currentlyApproved = await db.ResearchCalibrationProfileRecords
                    .Include(item => item.ApprovalHistory)
                    .Where(item => item.Id != record.Id
                        && item.InstrumentId == record.InstrumentId
                        && item.StrategyId == record.StrategyId
                        && item.HorizonSeconds == record.HorizonSeconds
                        && item.Status == ResearchCalibrationStatus.Approved)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var previous in currentlyApproved)
                {
                    previous.Status = ResearchCalibrationStatus.Superseded;
                    previous.Revision++;
                    previous.DecidedAtUtc = now;
                    previous.ApprovalHistory.Add(new ResearchCalibrationApprovalRecord
                    {
                        Revision = previous.Revision,
                        Action = "Superseded",
                        Reviewer = reviewer,
                        Reason = reason,
                        ExpectedOutputSha256 = previous.OutputSha256,
                        RelatedProfileId = record.Id,
                        CreatedAtUtc = now
                    });
                }
                record.Status = ResearchCalibrationStatus.Approved;
            }
            else
            {
                record.Status = ResearchCalibrationStatus.Rejected;
            }
            record.Revision++;
            record.DecidedAtUtc = now;
            record.ApprovalHistory.Add(new ResearchCalibrationApprovalRecord
            {
                Revision = record.Revision,
                Action = request.Decision == ResearchCalibrationDecision.Approve ? "Approved" : "Rejected",
                Reviewer = reviewer,
                Reason = reason,
                ExpectedOutputSha256 = record.OutputSha256,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }

        public async Task<OpportunityRankingResult> RankAsync(
            Guid id,
            OpportunityRankingRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var candidates = request.Candidates ?? Array.Empty<OpportunityRankingCandidate>();
            if (candidates.Count == 0) throw new ArgumentException("At least one ranking candidate is required.", nameof(request));
            if (candidates.Count > _settings.MaximumRankingCandidates)
            {
                throw new ArgumentException($"Ranking request exceeds the maximum of {_settings.MaximumRankingCandidates} candidates.", nameof(request));
            }
            if (candidates.Any(item => string.IsNullOrWhiteSpace(item.CandidateKey))
                || candidates.Select(item => item.CandidateKey.Trim()).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            {
                throw new ArgumentException("Ranking candidate keys must be non-empty and unique.", nameof(request));
            }
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.ResearchCalibrationProfileRecords.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Research calibration profile '{id}' was not found.");
            if (record.Status != ResearchCalibrationStatus.Approved)
            {
                throw new InvalidOperationException("Only a manually approved calibration profile may rank opportunities.");
            }
            if (!string.Equals(request.FeatureVersion, record.FeatureVersion, StringComparison.Ordinal)
                || !string.Equals(request.PatternVersion, record.PatternVersion, StringComparison.Ordinal)
                || !string.Equals(request.LabelVersion, record.LabelVersion, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Ranking pipeline versions do not match the approved calibration profile.");
            }
            if (!string.Equals(record.OutputSha256, ComputeOutputHash(record), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Approved calibration profile failed its output hash verification.");
            }
            var points = Deserialize<ProbabilityCalibrationPoint[]>(record.CalibrationPointsJson);
            var scored = candidates.Select(candidate => Score(record, points, candidate)).ToArray();
            var ordered = scored
                .OrderByDescending(item => item.Eligible)
                .ThenByDescending(item => item.ExpectedNetReturnBps)
                .ThenByDescending(item => item.CalibratedProbability)
                .ThenByDescending(item => item.RawConfidence)
                .ThenBy(item => item.CandidateKey, StringComparer.Ordinal)
                .Select((item, index) => item with { Rank = index + 1 })
                .ToArray();
            return new OpportunityRankingResult(record.Id, record.ModelVersion, record.CalibrationVersion, record.OutputSha256, ordered);
        }

        private RankedOpportunity Score(
            ResearchCalibrationProfileRecord profile,
            IReadOnlyList<ProbabilityCalibrationPoint> points,
            OpportunityRankingCandidate candidate)
        {
            var reasons = new List<string>();
            if (candidate.RawConfidence is < 0m or > 1m) reasons.Add("Raw confidence must be between zero and one.");
            if (candidate.EstimatedCostBps < 0m) reasons.Add("Estimated cost cannot be negative.");
            if (!string.IsNullOrWhiteSpace(profile.InstrumentId)
                && !string.Equals(profile.InstrumentId, candidate.InstrumentId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("Instrument is outside the approved profile scope.");
            }
            if (!string.IsNullOrWhiteSpace(profile.StrategyId)
                && !string.Equals(profile.StrategyId, candidate.StrategyId?.Trim(), StringComparison.Ordinal))
            {
                reasons.Add("Strategy is outside the approved profile scope.");
            }
            var probability = candidate.RawConfidence is >= 0m and <= 1m ? _calculator.Predict(points, candidate.RawConfidence) : 0m;
            var incrementalCost = Math.Max(0m, candidate.EstimatedCostBps - profile.AverageEstimatedCostBps);
            var expectedNetReturn = probability * profile.AverageWinBps + (1m - probability) * profile.AverageLossBps - incrementalCost;
            if (candidate.RawConfidence < profile.StableConfidenceThreshold) reasons.Add("Raw confidence is below the approved stable threshold.");
            if (probability < profile.MinimumCalibratedProbability) reasons.Add("Calibrated probability is below the approved minimum.");
            if (expectedNetReturn <= 0m) reasons.Add("Expected net return after incremental cost is not positive.");
            return new RankedOpportunity(
                0,
                candidate.CandidateKey.Trim(),
                candidate.InstrumentId?.Trim() ?? string.Empty,
                candidate.StrategyId?.Trim() ?? string.Empty,
                candidate.RawConfidence,
                probability,
                expectedNetReturn,
                reasons.Count == 0,
                reasons,
                ToUtc(candidate.ObservedAtUtc));
        }

        private static async Task<ResearchEvaluationObservation[]> LoadObservationsAsync(
            TradingBotDbContext db,
            ResearchEvaluationRunSnapshot evaluation,
            CancellationToken cancellationToken)
        {
            var query = db.CandidateLabelRecords.AsNoTracking()
                .Where(label => label.HorizonSeconds == evaluation.HorizonSeconds)
                .Where(label => label.Status == CandidateLabelStatus.Complete || label.Status == CandidateLabelStatus.Ambiguous)
                .Where(label => label.GrossReturnBps.HasValue && label.EstimatedCostBps.HasValue && label.NetReturnBps.HasValue
                    && label.MaximumFavorableExcursionBps.HasValue && label.MaximumAdverseExcursionBps.HasValue)
                .Where(label => label.ResearchCandidate.EvaluatedAtUtc >= evaluation.FromUtc && label.WindowEndUtc <= evaluation.ToUtc);
            query = evaluation.ReplayRunId.HasValue
                ? query.Where(label => label.ResearchCandidate.ReplayRunId == evaluation.ReplayRunId)
                : query.Where(label => label.ResearchCandidate.ReplayRunId == null);
            if (!string.IsNullOrWhiteSpace(evaluation.InstrumentId))
            {
                query = query.Where(label => label.ResearchCandidate.InstrumentId == evaluation.InstrumentId);
            }
            if (!string.IsNullOrWhiteSpace(evaluation.StrategyId))
            {
                query = query.Where(label => label.ResearchCandidate.StrategyId == evaluation.StrategyId);
            }
            return await query.OrderBy(label => label.ResearchCandidate.EvaluatedAtUtc)
                .ThenBy(label => label.ResearchCandidate.CandidateKey)
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
        }

        private static void ValidatePipelineVersions(
            IReadOnlyCollection<ResearchEvaluationObservation> rows,
            ResearchEvaluationRunSnapshot evaluation)
        {
            var eligible = rows.Where(item => item.Outcome != ResearchCandidateOutcome.Rejected).ToArray();
            if (eligible.Count(item => item.FeatureVersion != evaluation.FeatureVersion) > 0
                || eligible.Count(item => item.PatternVersion != evaluation.PatternVersion) > 0
                || eligible.Count(item => item.LabelVersion != evaluation.LabelVersion) > 0)
            {
                throw new InvalidOperationException("Calibration observations do not match the evaluation's feature, pattern and label versions.");
            }
        }

        private static string ComputeEvaluationInputHash(
            IEnumerable<ResearchEvaluationObservation> rows,
            ResearchEvaluationRunSnapshot evaluation)
        {
            var canonical = new StringBuilder();
            canonical.Append(evaluation.HorizonSeconds).Append('|').Append(evaluation.ReplayRunId).Append('|')
                .Append(evaluation.InstrumentId).Append('|').Append(evaluation.StrategyId).Append('|')
                .Append(evaluation.FromUtc.ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(evaluation.ToUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
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

        private static string ComputeOutputHash(ResearchCalibrationProfileRecord item) => Hash(
            $"{item.ProfileKey}|{item.ModelVersion}|{item.EvaluationRunId:N}|{item.StableConfidenceThreshold.ToString(CultureInfo.InvariantCulture)}|{item.MinimumCalibratedProbability.ToString(CultureInfo.InvariantCulture)}|{item.AverageWinBps.ToString(CultureInfo.InvariantCulture)}|{item.AverageLossBps.ToString(CultureInfo.InvariantCulture)}|{item.AverageEstimatedCostBps.ToString(CultureInfo.InvariantCulture)}|{item.ThresholdStabilityJson}|{item.CalibrationPointsJson}|{item.WalkForwardRawMetricsJson}|{item.WalkForwardCalibratedMetricsJson}|{item.HoldoutRawMetricsJson}|{item.HoldoutCalibratedMetricsJson}|{item.BaselineComparisonJson}");

        private static ResearchCalibrationProfileSnapshot ToSnapshot(ResearchCalibrationProfileRecord item)
        {
            var comparison = Deserialize<CalibrationBaselineComparison>(item.BaselineComparisonJson);
            return new ResearchCalibrationProfileSnapshot(
                item.Id,
                item.ProfileKey,
                item.ModelVersion,
                item.Revision,
                item.Status,
                item.EvaluationRunId,
                item.ReplayRunId,
                item.InstrumentId,
                item.StrategyId,
                item.HorizonSeconds,
                item.CalibrationVersion,
                item.EvaluationVersion,
                item.FeatureVersion,
                item.PatternVersion,
                item.LabelVersion,
                item.StableConfidenceThreshold,
                item.MinimumCalibratedProbability,
                item.AverageWinBps,
                item.AverageLossBps,
                item.AverageEstimatedCostBps,
                Deserialize<ThresholdStabilityResult>(item.ThresholdStabilityJson),
                Deserialize<ProbabilityCalibrationPoint[]>(item.CalibrationPointsJson),
                Deserialize<ProbabilityCalibrationMetrics>(item.WalkForwardRawMetricsJson),
                Deserialize<ProbabilityCalibrationMetrics>(item.WalkForwardCalibratedMetricsJson),
                Deserialize<ProbabilityCalibrationMetrics>(item.HoldoutRawMetricsJson),
                Deserialize<ProbabilityCalibrationMetrics>(item.HoldoutCalibratedMetricsJson),
                comparison,
                item.Status == ResearchCalibrationStatus.Draft && comparison.PassesApprovalGate,
                item.InputSha256,
                item.OutputSha256,
                item.CreatedAtUtc,
                item.DecidedAtUtc,
                item.ApprovalHistory.OrderBy(history => history.Id).Select(history => new ResearchCalibrationApprovalSnapshot(
                    history.Id,
                    history.Revision,
                    history.Action,
                    history.Reviewer,
                    history.Reason,
                    history.ExpectedOutputSha256,
                    history.RelatedProfileId,
                    history.CreatedAtUtc)).ToArray());
        }

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
        private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions)
            ?? throw new InvalidDataException($"Persisted calibration payload '{typeof(T).Name}' is invalid.");
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
