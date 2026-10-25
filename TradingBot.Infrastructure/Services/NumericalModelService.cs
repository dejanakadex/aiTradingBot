using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class NumericalModelService : INumericalModelService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly NumericalModelSettings _settings;
        private readonly IClock _clock;
        private readonly string _currentLabelVersion;
        private readonly string _currentFeatureVersion;
        private readonly ConcurrentDictionary<(string InstrumentId, string StrategyId), CachedModel> _cache = new();
        private readonly SemaphoreSlim _cacheLoadLock = new(1, 1);
        private long _cacheEpoch;
        private readonly ForexResearchContract? _forexContract;

        public NumericalModelService(IDbContextFactory<TradingBotDbContext> dbFactory, IOptions<NumericalModelSettings> settings, IClock clock,
            IOptions<CandidateLabelingSettings>? labelSettings = null, IOptions<CanonicalFeatureSettings>? featureSettings = null,
            ForexResearchContract? forexContract = null)
        {
            _dbFactory = dbFactory;
            _settings = settings.Value;
            _clock = clock;
            _forexContract = forexContract;
            _currentLabelVersion = CandidateLabelVersion.Create(labelSettings?.Value ?? new CandidateLabelingSettings());
            _currentFeatureVersion = new FeatureEngine(featureSettings?.Value ?? new CanonicalFeatureSettings()).FeatureVersion;
        }

        public async Task<NumericalModelSnapshot> TrainAsync(NumericalModelTrainingRequest request, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var evaluation = await db.ResearchEvaluationRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.EvaluationRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Research evaluation '{request.EvaluationRunId}' was not found.");
            var forex = evaluation.LabelVersion.StartsWith(ForexResearchContract.Labels + "+config-", StringComparison.Ordinal);
            if (!(evaluation.LabelVersion.StartsWith(PipelineContractVersions.Labels + "+config-", StringComparison.Ordinal) || forex)
                || !evaluation.EvaluationVersion.StartsWith(ResearchEvaluationVersion.Contract + "+config-", StringComparison.Ordinal))
                throw new InvalidDataException("Model training requires current evaluation and covered-label contracts; legacy horizon-return labels cannot train a new model.");
            if (forex)
            {
                var contract = _forexContract?.Get(evaluation.InstrumentId, evaluation.StrategyId)
                    ?? throw new InvalidOperationException("FX model training requires its current research contract.");
                if (evaluation.FeatureVersion != contract.Feature || evaluation.PatternVersion != contract.Pattern
                    || evaluation.LabelVersion != contract.Label || evaluation.HorizonSeconds != contract.Profile.HoldingSeconds)
                    throw new InvalidDataException("FX evaluation contract differs from configured research policy.");
                var replay = await db.ReplayRunRecords.SingleOrDefaultAsync(x => x.Id == evaluation.ReplayRunId, cancellationToken);
                if (replay?.Status != ReplayRunStatus.Completed || replay.OutputSha256 != await DeterministicReplayService.ComputeOutputHashAsync(db, replay.Id, cancellationToken))
                    throw new InvalidDataException("FX training requires an intact completed replay.");
            }
            var existing = await db.NumericalModelRecords.AsNoTracking().SingleOrDefaultAsync(x => x.EvaluationRunId == evaluation.Id, cancellationToken).ConfigureAwait(false);

            var evaluationQuery = db.CandidateLabelRecords.AsNoTracking()
                .Where(x => x.HorizonSeconds == evaluation.HorizonSeconds && x.Status == CandidateLabelStatus.Complete
                    && x.GrossReturnBps.HasValue && x.EstimatedCostBps.HasValue && x.NetReturnBps.HasValue
                    && x.MaximumFavorableExcursionBps.HasValue && x.MaximumAdverseExcursionBps.HasValue
                    && x.ResearchCandidate.EvaluatedAtUtc >= evaluation.FromUtc && x.WindowEndUtc <= evaluation.ToUtc);
            evaluationQuery = evaluation.ReplayRunId.HasValue
                ? evaluationQuery.Where(x => x.ResearchCandidate.ReplayRunId == evaluation.ReplayRunId)
                : evaluationQuery.Where(x => x.ResearchCandidate.ReplayRunId == null);
            if (!string.IsNullOrWhiteSpace(evaluation.InstrumentId))
                evaluationQuery = evaluationQuery.Where(x => x.ResearchCandidate.InstrumentId == evaluation.InstrumentId);
            if (!string.IsNullOrWhiteSpace(evaluation.StrategyId))
                evaluationQuery = evaluationQuery.Where(x => x.ResearchCandidate.StrategyId == evaluation.StrategyId);
            var evaluationRows = await evaluationQuery.OrderBy(x => x.ResearchCandidate.EvaluatedAtUtc)
                .ThenBy(x => x.ResearchCandidate.CandidateKey)
                .Select(x => new ResearchEvaluationObservation(
                    x.ResearchCandidateId, x.ResearchCandidate.CandidateKey, x.ResearchCandidate.InstrumentId,
                    x.ResearchCandidate.StrategyId, x.ResearchCandidate.PatternType, x.ResearchCandidate.Direction,
                    x.ResearchCandidate.Timeframe, x.ResearchCandidate.EvaluatedAtUtc, x.WindowEndUtc,
                    x.ResearchCandidate.Confidence, x.ResearchCandidate.Outcome, x.ResearchCandidate.MarketRegime,
                    x.ResearchCandidate.NormalizedLiquidity, x.GrossReturnBps!.Value, x.EstimatedCostBps!.Value,
                    x.NetReturnBps!.Value, x.MaximumFavorableExcursionBps!.Value, x.MaximumAdverseExcursionBps!.Value,
                    x.TargetStopOutcome, x.ResearchCandidate.FeatureVersion, x.ResearchCandidate.PatternVersion,
                    x.LabelVersion, x.InputSha256))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var evaluationRequest = new ResearchEvaluationRequest(evaluation.HorizonSeconds, evaluation.FromUtc,
                evaluation.ToUtc, evaluation.ReplayRunId, evaluation.InstrumentId, evaluation.StrategyId);
            var evaluationInputHash = ResearchEvaluationService.ComputeInputHash(evaluationRows, evaluationRequest,
                ToUtc(evaluation.FromUtc), ToUtc(evaluation.ToUtc));
            if (!string.Equals(evaluation.InputSha256, evaluationInputHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Evaluation input changed after the frozen run; create a new evaluation before model training.");
            if (existing != null)
            {
                if (existing.OutputSha256 != OutputHash(existing))
                    throw new InvalidDataException("Existing model artifact failed hash verification.");
                return ToSnapshot(existing);
            }

            var query = db.CandidateLabelRecords.AsNoTracking()
                .Where(x => x.HorizonSeconds == evaluation.HorizonSeconds
                    && x.Status == CandidateLabelStatus.Complete
                    && x.NetReturnBps.HasValue && x.ResearchCandidate.Outcome != ResearchCandidateOutcome.Rejected
                    && x.ResearchCandidate.EvaluatedAtUtc >= evaluation.FromUtc && x.WindowEndUtc <= evaluation.ToUtc);
            query = evaluation.ReplayRunId.HasValue
                ? query.Where(x => x.ResearchCandidate.ReplayRunId == evaluation.ReplayRunId)
                : query.Where(x => x.ResearchCandidate.ReplayRunId == null);
            if (!string.IsNullOrWhiteSpace(evaluation.InstrumentId)) query = query.Where(x => x.ResearchCandidate.InstrumentId == evaluation.InstrumentId);
            if (!string.IsNullOrWhiteSpace(evaluation.StrategyId)) query = query.Where(x => x.ResearchCandidate.StrategyId == evaluation.StrategyId);
            var sourceRows = await query.OrderBy(x => x.ResearchCandidate.EvaluatedAtUtc).ThenBy(x => x.ResearchCandidate.CandidateKey)
                .Select(x => new
                {
                    x.ResearchCandidateId, x.ResearchCandidate.CandidateKey, x.ResearchCandidate.EvaluatedAtUtc, LabelWindowEndUtc = x.WindowEndUtc,
                    x.ResearchCandidate.Confidence, x.ResearchCandidate.PatternType, x.ResearchCandidate.Direction, x.ResearchCandidate.Timeframe,
                    x.ResearchCandidate.NormalizedLiquidity, x.ResearchCandidate.NormalizedVolatility,
                    x.ResearchCandidate.FeatureVersion, x.ResearchCandidate.PatternVersion, x.LabelVersion,
                    x.InputSha256, NetReturnBps = x.NetReturnBps!.Value, GrossReturnBps = x.GrossReturnBps ?? 0m
                }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (sourceRows.Any(x => forex && (x.NormalizedLiquidity.HasValue || !x.NormalizedVolatility.HasValue || x.NormalizedVolatility < 0)
                || string.IsNullOrWhiteSpace(x.InputSha256)
                || x.FeatureVersion != evaluation.FeatureVersion || x.PatternVersion != evaluation.PatternVersion
                || x.LabelVersion != evaluation.LabelVersion))
                throw new InvalidDataException("Model training requires frozen label input hashes and the exact evaluated pipeline versions.");
            var rows = sourceRows.Select(x => new ModelObservation
            {
                CandidateId = x.ResearchCandidateId, CandidateKey = x.CandidateKey, EvaluatedAtUtc = x.EvaluatedAtUtc, LabelWindowEndUtc = x.LabelWindowEndUtc,
                LabelInputSha256 = x.InputSha256,
                PatternConfidence = (float)x.Confidence, PatternType = (float)x.PatternType, Direction = (float)x.Direction, Timeframe = (float)x.Timeframe,
                NormalizedLiquidity = (float)(x.NormalizedLiquidity ?? 0m), NormalizedVolatility = (float)(x.NormalizedVolatility ?? 0m),
                HourSin = (float)Math.Sin(2d * Math.PI * x.EvaluatedAtUtc.Hour / 24d), HourCos = (float)Math.Cos(2d * Math.PI * x.EvaluatedAtUtc.Hour / 24d),
                NetReturnBps = x.NetReturnBps, GrossReturnBps = (float)x.GrossReturnBps, Label = x.NetReturnBps > 0m
            }).ToArray();
            if (rows.Length < _settings.MinimumTrainingSamples + _settings.MinimumHoldoutSelections)
                throw new InvalidOperationException($"Model training has {rows.Length} samples; more labeled samples are required.");
            var preHoldout = rows.Where(x => x.LabelWindowEndUtc <= evaluation.HoldoutStartUtc).ToArray();
            var holdout = rows.Where(x => x.EvaluatedAtUtc >= evaluation.HoldoutStartUtc).ToArray();
            if (preHoldout.Length < _settings.MinimumTrainingSamples || holdout.Length < _settings.MinimumHoldoutSelections)
                throw new InvalidOperationException("Training or untouched holdout sample count is below the configured minimum.");

            var folds = JsonSerializer.Deserialize<TradingBot.Domain.Models.WalkForwardFoldResult[]>(evaluation.FoldsJson, JsonOptions) ?? [];
            var baselineWfSelected = new List<ModelObservation>();
            var candidateWfSelected = new List<ModelObservation>();
            var validationScores = new List<ScoredObservation>();
            var baselineForecastErrors = new List<decimal>();
            foreach (var fold in folds.Where(x => x.SelectedConfidenceThreshold.HasValue))
            {
                var training = rows.Where(x => x.EvaluatedAtUtc >= xDate(fold.TrainingFromUtc) && x.LabelWindowEndUtc <= xDate(fold.TrainingToUtc)).ToArray();
                var validation = rows.Where(x => x.EvaluatedAtUtc >= xDate(fold.ValidationFromUtc) && x.LabelWindowEndUtc <= xDate(fold.ValidationToUtc)).ToArray();
                if (training.Length < _settings.MinimumTrainingSamples || validation.Length == 0) continue;
                baselineWfSelected.AddRange(validation.Where(x => x.PatternConfidence >= (float)fold.SelectedConfidenceThreshold!.Value));
                var (model, _) = Train(training, forex);
                var trainingScores = Score(model, training);
                var threshold = SelectThreshold(trainingScores);
                var foldValidationScores = Score(model, validation);
                if (forex) baselineForecastErrors.AddRange(validation.Select(x => Math.Abs((decimal)x.GrossReturnBps - training.Average(t => (decimal)t.GrossReturnBps))));
                validationScores.AddRange(foldValidationScores);
                candidateWfSelected.AddRange(foldValidationScores.Where(x => x.Probability >= threshold).Select(x => x.Observation));
            }
            if (candidateWfSelected.Count < _settings.MinimumValidationSelections)
                throw new InvalidOperationException("Walk-forward folds did not produce enough candidate model selections.");

            var (finalModel, finalSchema) = Train(preHoldout, forex);
            // The deployed cutoff is selected from out-of-sample fold predictions, never from fitted training scores.
            var probabilityThreshold = SelectThreshold(validationScores.GroupBy(x => x.Observation.CandidateId).Select(group => group.First()));
            var holdoutScores = Score(finalModel, holdout);
            var candidateHoldoutSelected = holdoutScores.Where(x => x.Probability >= probabilityThreshold).Select(x => x.Observation).ToArray();
            var baselineHoldoutSelected = holdout.Where(x => x.PatternConfidence >= (float)evaluation.SelectedConfidenceThreshold).ToArray();
            var baselineWf = Metrics(baselineWfSelected);
            var candidateWf = Metrics(candidateWfSelected);
            var baselineHoldout = Metrics(baselineHoldoutSelected);
            var candidateHoldout = Metrics(candidateHoldoutSelected);
            var approvalReasons = ApprovalReasons(baselineWf, candidateWf, baselineHoldout, candidateHoldout).ToList();
            if (forex)
            {
                baselineWf = baselineWf with { ForecastMeanAbsoluteErrorBps = baselineForecastErrors.Average() };
                candidateWf = candidateWf with { ForecastMeanAbsoluteErrorBps = validationScores.Average(x => Math.Abs(x.ExpectedGross!.Value - (decimal)x.Observation.GrossReturnBps)) };
                var mean = preHoldout.Average(x => (decimal)x.GrossReturnBps);
                baselineHoldout = baselineHoldout with { ForecastMeanAbsoluteErrorBps = holdout.Average(x => Math.Abs((decimal)x.GrossReturnBps - mean)) };
                candidateHoldout = candidateHoldout with { ForecastMeanAbsoluteErrorBps = holdoutScores.Average(x => Math.Abs(x.ExpectedGross!.Value - (decimal)x.Observation.GrossReturnBps)) };
                if (candidateWf.ForecastMeanAbsoluteErrorBps >= baselineWf.ForecastMeanAbsoluteErrorBps
                    || candidateHoldout.ForecastMeanAbsoluteErrorBps >= baselineHoldout.ForecastMeanAbsoluteErrorBps)
                    approvalReasons.Add("FX gross-return forecast must beat the past-only constant baseline on walk-forward and untouched holdout MAE.");
            }

            using var artifact = new MemoryStream();
            new MLContext(seed: _settings.RandomSeed).Model.Save(finalModel, finalSchema, artifact);
            var artifactBytes = artifact.ToArray();
            var inputHash = Hash(string.Join('\n', rows.Select(x => $"{x.CandidateId}|{x.CandidateKey}|{x.EvaluatedAtUtc:O}|{x.PatternConfidence:R}|{x.NormalizedLiquidity:R}|{x.NormalizedVolatility:R}|{x.NetReturnBps.ToString(CultureInfo.InvariantCulture)}|{x.LabelInputSha256}")));
            var version = await db.NumericalModelRecords.Where(x => x.InstrumentId == evaluation.InstrumentId && x.StrategyId == evaluation.StrategyId)
                .Select(x => (int?)x.ModelVersion).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            if (forex) inputHash = Hash(inputHash + "|" + evaluation.InputSha256 + "|" + evaluation.OutputSha256
                + "|" + Serialize(_settings) + "|" + ForexResearchContract.Model);
            var record = new NumericalModelRecord
            {
                Id = Guid.NewGuid(), ModelVersion = version + 1, Status = NumericalModelStatus.Draft, EvaluationRunId = evaluation.Id,
                InstrumentId = evaluation.InstrumentId, StrategyId = evaluation.StrategyId, HorizonSeconds = evaluation.HorizonSeconds,
                Algorithm = forex ? "ML.NET LightGBM net-win classification and execution gross-return regression" : "ML.NET LightGBM binary classification",
                ModelVersionTag = forex ? ForexResearchContract.Model : "numerical-model-v2",
                FeatureVersion = evaluation.FeatureVersion, PatternVersion = evaluation.PatternVersion, LabelVersion = evaluation.LabelVersion,
                ProbabilityThreshold = probabilityThreshold, BaselineWalkForwardJson = Serialize(baselineWf), CandidateWalkForwardJson = Serialize(candidateWf),
                BaselineHoldoutJson = Serialize(baselineHoldout), CandidateHoldoutJson = Serialize(candidateHoldout),
                ApprovalReady = approvalReasons.Count == 0, ApprovalReasonsJson = Serialize(approvalReasons), ModelArtifact = artifactBytes,
                InputSha256 = inputHash, CreatedAtUtc = ToUtc(_clock.UtcNow)
            };
            record.OutputSha256 = OutputHash(record);
            db.NumericalModelRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }

        public async Task<NumericalModelSnapshot> DecideAsync(Guid id, NumericalModelDecisionRequest request, CancellationToken cancellationToken = default)
        {
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(request.Reviewer) || reason.Length < 10) throw new ArgumentException("Reviewer and a decision reason of at least 10 characters are required.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.NumericalModelRecords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Numerical model '{id}' was not found.");
            if (record.Status != NumericalModelStatus.Draft) throw new InvalidOperationException("Only a Draft model can receive a decision.");
            if (!string.Equals(record.OutputSha256, request.ExpectedOutputSha256, StringComparison.Ordinal) || record.OutputSha256 != OutputHash(record))
                throw new InvalidDataException("Model output hash does not match the reviewed artifact.");
            if (request.Approve && !record.ApprovalReady) throw new InvalidOperationException($"Model did not beat baseline gates: {string.Join(" ", Deserialize<string[]>(record.ApprovalReasonsJson))}");
            if (request.Approve) ValidateRuntimeContract(record, record.FeatureVersion);
            var decidedAt = ToUtc(_clock.UtcNow);
            if (request.Approve)
            {
                var previous = await db.NumericalModelRecords.Where(x => x.Id != id && x.InstrumentId == record.InstrumentId && x.StrategyId == record.StrategyId && x.Status == NumericalModelStatus.Approved).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in previous)
                {
                    AddDecision(db, item, NumericalModelStatus.Superseded, "Superseded", request.Reviewer.Trim(), reason, decidedAt);
                    item.Status = NumericalModelStatus.Superseded;
                }
            }
            AddDecision(db, record, request.Approve ? NumericalModelStatus.Approved : NumericalModelStatus.Rejected,
                request.Approve ? "Approve" : "Reject", request.Reviewer.Trim(), reason, decidedAt);
            record.Status = request.Approve ? NumericalModelStatus.Approved : NumericalModelStatus.Rejected;
            record.Reviewer = request.Reviewer.Trim(); record.DecisionReason = reason; record.DecidedAtUtc = decidedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _cacheEpoch);
            _cache.TryRemove((record.InstrumentId, record.StrategyId), out _);
            return ToSnapshot(record);
        }

        public async Task<NumericalModelSnapshot> RollbackAsync(Guid id, NumericalModelRollbackRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var reviewer = request.Reviewer?.Trim() ?? string.Empty;
            var reason = request.Reason?.Trim() ?? string.Empty;
            if (reviewer.Length == 0 || reason.Length < 10) throw new ArgumentException("Reviewer and a rollback reason of at least 10 characters are required.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var target = await db.NumericalModelRecords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Numerical model '{id}' was not found.");
            if (target.Status != NumericalModelStatus.Superseded || !target.ApprovalReady)
                throw new InvalidOperationException("Rollback requires a previously approved, superseded model that passed approval gates.");
            if (!string.Equals(target.OutputSha256, request.ExpectedOutputSha256, StringComparison.Ordinal)
                || target.OutputSha256 != OutputHash(target))
                throw new InvalidDataException("Rollback target hash does not match the reviewed artifact.");
            ValidateRuntimeContract(target, target.FeatureVersion);
            var current = await db.NumericalModelRecords.SingleOrDefaultAsync(x => x.InstrumentId == target.InstrumentId
                && x.StrategyId == target.StrategyId && x.Status == NumericalModelStatus.Approved, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No current approved model exists to replace by rollback.");
            var decidedAt = ToUtc(_clock.UtcNow);
            AddDecision(db, current, NumericalModelStatus.Superseded, "SupersededByRollback", reviewer, reason, decidedAt);
            AddDecision(db, target, NumericalModelStatus.Approved, "Rollback", reviewer, reason, decidedAt);
            current.Status = NumericalModelStatus.Superseded;
            target.Status = NumericalModelStatus.Approved;
            target.Reviewer = reviewer;
            target.DecisionReason = reason;
            target.DecidedAtUtc = decidedAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _cacheEpoch);
            _cache.TryRemove((target.InstrumentId, target.StrategyId), out _);
            return ToSnapshot(target);
        }

        public async Task<IReadOnlyList<NumericalModelDecisionSnapshot>> GetDecisionHistoryAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.NumericalModelDecisionRecords.AsNoTracking().Where(x => x.ModelId == id)
                .OrderBy(x => x.DecidedAtUtc).ThenBy(x => x.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (rows.Length == 0 && !await db.NumericalModelRecords.AsNoTracking()
                .AnyAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException($"Numerical model '{id}' was not found.");
            return rows.Select(x => new NumericalModelDecisionSnapshot(x.Id, x.ModelId, x.InstrumentId, x.StrategyId,
                x.StatusBefore, x.StatusAfter, x.Action, x.Reviewer, x.Reason, x.ReviewedOutputSha256, x.DecidedAtUtc)).ToArray();
        }

        public async Task<NumericalModelPrediction> PredictAsync(NumericalModelPredictionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.InstrumentId) || string.IsNullOrWhiteSpace(request.StrategyId))
                throw new ArgumentException("Instrument and strategy IDs are required.", nameof(request));
            var cached = await GetCachedModelAsync((request.InstrumentId, request.StrategyId), cancellationToken).ConfigureAwait(false);
            var record = cached.Record;
            ValidateRuntimeContract(record, request.FeatureVersion);
            var forex = record.ModelVersionTag == ForexResearchContract.Model;
            var forexInterval = forex ? _forexContract!.Get(request.InstrumentId, request.StrategyId).Profile.IntervalSeconds : 0;
            if (forex && (request.PatternVersion != record.PatternVersion || request.ExecutionLabelVersion != record.LabelVersion
                || request.Direction != (int)TradeDirection.Long || request.NormalizedLiquidity != 0 || request.NormalizedVolatility < 0
                || request.EstimatedCostBps < 0 || request.ObservedAtUtc.Kind != DateTimeKind.Utc
                || request.Timeframe != (int)(forexInterval == 1 ? Timeframe.OneSecond : forexInterval == 5 ? Timeframe.FiveSeconds : Timeframe.FifteenSeconds)))
                throw new InvalidOperationException("FX inference requires exact pattern/execution contracts, long direction and price-only features.");
            var engine = cached.Engines.TryTake(out var available)
                ? available
                : cached.Ml.Model.CreatePredictionEngine<ModelRow, ModelPrediction>(cached.Model, ignoreMissingColumns: true);
            var row = FromRequest(request);
            decimal probability;
            decimal? expectedGross = null;
            try
            {
                var prediction = engine.Predict(row);
                if (!float.IsFinite(prediction.Probability) || forex && !float.IsFinite(prediction.ExpectedGrossReturnBps))
                    throw new InvalidDataException("Model produced a non-finite prediction.");
                probability = (decimal)prediction.Probability;
                if (forex) expectedGross = (decimal)prediction.ExpectedGrossReturnBps;
            }
            finally { cached.Engines.Add(engine); }
            var eligible = probability >= record.ProbabilityThreshold && (!forex || expectedGross > request.EstimatedCostBps);
            return new NumericalModelPrediction(record.Id, record.ModelVersion, probability, record.ProbabilityThreshold,
                cached.HistoricalSelectedNetReturnBps, record.HorizonSeconds, eligible,
                eligible ? "Approved local model passed its probability and applicable cost thresholds." : "Local model probability or expected edge failed.")
                { ExpectedGrossReturnBps = expectedGross };
        }

        private async Task<CachedModel> GetCachedModelAsync((string InstrumentId, string StrategyId) key, CancellationToken cancellationToken)
        {
            var now = ToUtc(_clock.UtcNow);
            if (_cache.TryGetValue(key, out var hit) && hit.Epoch == Interlocked.Read(ref _cacheEpoch)
                && hit.RefreshAfterUtc > now) return hit;
            await _cacheLoadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                now = ToUtc(_clock.UtcNow);
                var epoch = Interlocked.Read(ref _cacheEpoch);
                if (_cache.TryGetValue(key, out hit) && hit.Epoch == epoch && hit.RefreshAfterUtc > now) return hit;
                // A failed refresh never serves a stale approved model.
                _cache.TryRemove(key, out _);
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
                var identities = await db.NumericalModelRecords.AsNoTracking()
                    .Where(x => x.Status == NumericalModelStatus.Approved && x.InstrumentId == key.InstrumentId && x.StrategyId == key.StrategyId)
                    .Select(x => new { x.Id, x.OutputSha256 })
                    .Take(2).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (identities.Length != 1) throw new InvalidOperationException("Exactly one approved numerical model must match the instrument and strategy.");
                if (hit != null && hit.Epoch == epoch && hit.Record.Id == identities[0].Id
                    && hit.Record.OutputSha256 == identities[0].OutputSha256)
                {
                    hit.Extend(now.AddSeconds(_settings.CacheRefreshSeconds));
                    _cache[key] = hit;
                    return hit;
                }
                var record = await db.NumericalModelRecords.AsNoTracking().SingleAsync(x => x.Id == identities[0].Id
                    && x.Status == NumericalModelStatus.Approved, cancellationToken).ConfigureAwait(false);
                ValidateRuntimeContract(record, record.FeatureVersion);
                if (record.OutputSha256 != OutputHash(record)) throw new InvalidDataException("Approved model artifact failed hash verification.");
                var ml = new MLContext(seed: _settings.RandomSeed);
                using var stream = new MemoryStream(record.ModelArtifact, writable: false);
                var model = ml.Model.Load(stream, out _);
                var historicalSelectedNetReturn = Deserialize<NumericalModelMetrics>(record.CandidateHoldoutJson).ExpectancyBps;
                var loaded = new CachedModel(record, ml, model, historicalSelectedNetReturn,
                    now.AddSeconds(_settings.CacheRefreshSeconds), epoch);
                _cache[key] = loaded;
                return loaded;
            }
            finally { _cacheLoadLock.Release(); }
        }

        private void ValidateRuntimeContract(NumericalModelRecord record, string featureVersion)
        {
            if (record.ModelVersionTag == ForexResearchContract.Model)
            {
                var c = _forexContract?.Get(record.InstrumentId, record.StrategyId)
                    ?? throw new InvalidOperationException("FX runtime research contract is unavailable.");
                if (record.PatternVersion != c.Pattern || record.FeatureVersion != c.Feature || featureVersion != c.Feature
                    || record.LabelVersion != c.Label || record.HorizonSeconds != c.Profile.HoldingSeconds)
                    throw new InvalidOperationException("FX model versions, horizon or execution assumptions differ; retrain and review.");
                return;
            }
            if (record.ModelVersionTag != "numerical-model-v2"
                || record.PatternVersion != PipelineContractVersions.Patterns
                || record.LabelVersion != _currentLabelVersion)
                throw new InvalidOperationException("Model contract differs from the current pattern or label configuration; retrain and approve a compatible model.");
            if (record.FeatureVersion != _currentFeatureVersion
                && !record.FeatureVersion.StartsWith(_currentFeatureVersion + "+short-", StringComparison.Ordinal))
                throw new InvalidOperationException("Model feature contract differs from the current feature configuration.");
            if (record.FeatureVersion != featureVersion)
                throw new InvalidOperationException("Runtime feature version does not match the approved model.");
        }

        private static void AddDecision(TradingBotDbContext db, NumericalModelRecord model, NumericalModelStatus after,
            string action, string reviewer, string reason, DateTime decidedAt)
        {
            db.NumericalModelDecisionRecords.Add(new NumericalModelDecisionRecord
            {
                ModelId = model.Id, InstrumentId = model.InstrumentId, StrategyId = model.StrategyId,
                StatusBefore = model.Status, StatusAfter = after, Action = action, Reviewer = reviewer,
                Reason = reason, ReviewedOutputSha256 = model.OutputSha256, DecidedAtUtc = decidedAt
            });
        }

        public async Task<NumericalModelSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.NumericalModelRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
            return record == null ? null : ToSnapshot(record);
        }

        public async Task<IReadOnlyList<NumericalModelSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var records = await db.NumericalModelRecords.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(Math.Clamp(count, 1, 500)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return records.Select(ToSnapshot).ToArray();
        }

        private (ITransformer Model, DataViewSchema Schema) Train(ModelObservation[] rows, bool forex)
        {
            var ml = new MLContext(seed: _settings.RandomSeed);
            var data = ml.Data.LoadFromEnumerable(rows.Select(ToModelRow));
            var columns = new[] { nameof(ModelObservation.PatternConfidence), nameof(ModelObservation.PatternType), nameof(ModelObservation.Direction), nameof(ModelObservation.Timeframe), nameof(ModelObservation.NormalizedLiquidity), nameof(ModelObservation.NormalizedVolatility), nameof(ModelObservation.HourSin), nameof(ModelObservation.HourCos) };
            IEstimator<ITransformer> pipeline = ml.Transforms.Concatenate("Features", columns.Where(x => !forex || x != nameof(ModelObservation.NormalizedLiquidity)).ToArray());
            if (forex) pipeline = pipeline.Append(ml.Regression.Trainers.LightGbm(new LightGbmRegressionTrainer.Options
                { LabelColumnName = nameof(ModelRow.GrossReturnBps), FeatureColumnName = "Features", NumberOfLeaves = _settings.NumberOfLeaves,
                    NumberOfIterations = _settings.NumberOfIterations, LearningRate = _settings.LearningRate,
                    MinimumExampleCountPerLeaf = _settings.MinimumExampleCountPerLeaf, Seed = _settings.RandomSeed }))
                .Append(ml.Transforms.CopyColumns("ExpectedGrossReturnBps", "Score"));
            pipeline = pipeline.Append(ml.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
                {
                    LabelColumnName = nameof(ModelObservation.Label), FeatureColumnName = "Features", NumberOfLeaves = _settings.NumberOfLeaves,
                    NumberOfIterations = _settings.NumberOfIterations, LearningRate = _settings.LearningRate,
                    MinimumExampleCountPerLeaf = _settings.MinimumExampleCountPerLeaf, Seed = _settings.RandomSeed
                }));
            return (pipeline.Fit(data), data.Schema);
        }

        private ScoredObservation[] Score(ITransformer model, ModelObservation[] rows)
        {
            var ml = new MLContext(seed: _settings.RandomSeed);
            var data = ml.Data.LoadFromEnumerable(rows.Select(ToModelRow));
            var transformed = model.Transform(data);
            var forex = transformed.Schema.Any(x => x.Name == "ExpectedGrossReturnBps");
            var predictions = ml.Data.CreateEnumerable<ModelPrediction>(transformed, reuseRowObject: false, ignoreMissingColumns: true).ToArray();
            return rows.Zip(predictions, (observation, prediction) => new ScoredObservation(observation,
                (decimal)prediction.Probability,
                forex ? (decimal)prediction.ExpectedGrossReturnBps : null)).ToArray();
        }

        private decimal SelectThreshold(IEnumerable<ScoredObservation> source)
        {
            var scored = source.ToArray();
            return _settings.ProbabilityThresholds.OrderBy(x => x).Select(threshold => new { Threshold = threshold, Metrics = Metrics(scored.Where(x => x.Probability >= threshold).Select(x => x.Observation)) })
                .Where(x => x.Metrics.SelectedCount >= _settings.MinimumValidationSelections)
                .OrderByDescending(x => x.Metrics.ExpectancyBps).ThenByDescending(x => x.Metrics.NetProfitBps).ThenBy(x => x.Threshold)
                .Select(x => (decimal?)x.Threshold).FirstOrDefault()
                ?? throw new InvalidOperationException("No probability threshold retains the minimum selected sample count.");
        }

        private IReadOnlyList<string> ApprovalReasons(NumericalModelMetrics baselineWf, NumericalModelMetrics candidateWf, NumericalModelMetrics baselineHoldout, NumericalModelMetrics candidateHoldout)
        {
            var reasons = new List<string>();
            if (candidateWf.SelectedCount < _settings.MinimumValidationSelections) reasons.Add("Candidate walk-forward selection count is too small.");
            if (candidateHoldout.SelectedCount < _settings.MinimumHoldoutSelections) reasons.Add("Candidate holdout selection count is too small.");
            if (candidateWf.ExpectancyBps < baselineWf.ExpectancyBps + _settings.MinimumWalkForwardExpectancyImprovementBps) reasons.Add("Candidate does not beat baseline walk-forward expectancy.");
            if (candidateHoldout.ExpectancyBps < baselineHoldout.ExpectancyBps + _settings.MinimumHoldoutExpectancyImprovementBps) reasons.Add("Candidate does not beat baseline holdout expectancy.");
            if (candidateWf.ExpectancyBps <= 0m || candidateHoldout.ExpectancyBps <= 0m) reasons.Add("Candidate expectancy must remain positive out of sample.");
            return reasons;
        }

        private static NumericalModelMetrics Metrics(IEnumerable<ModelObservation> source)
        {
            var rows = source.GroupBy(x => x.CandidateId).Select(x => x.First()).OrderBy(x => x.EvaluatedAtUtc).ToArray();
            if (rows.Length == 0) return new NumericalModelMetrics(0, 0, 0m, 0m, 0m, 0m);
            decimal cumulative = 0m, peak = 0m, drawdown = 0m;
            foreach (var row in rows) { cumulative += row.NetReturnBps; peak = Math.Max(peak, cumulative); drawdown = Math.Max(drawdown, peak - cumulative); }
            return new NumericalModelMetrics(rows.Length, rows.Length, rows.Count(x => x.NetReturnBps > 0m) / (decimal)rows.Length, rows.Average(x => x.NetReturnBps), cumulative, drawdown);
        }

        private static ModelRow FromRequest(NumericalModelPredictionRequest x) => new()
        {
            PatternConfidence = (float)x.PatternConfidence, PatternType = x.PatternType, Direction = x.Direction, Timeframe = x.Timeframe,
            NormalizedLiquidity = (float)x.NormalizedLiquidity, NormalizedVolatility = (float)x.NormalizedVolatility,
            HourSin = (float)Math.Sin(2d * Math.PI * x.ObservedAtUtc.Hour / 24d), HourCos = (float)Math.Cos(2d * Math.PI * x.ObservedAtUtc.Hour / 24d)
        };
        private static ModelRow ToModelRow(ModelObservation x) => new()
        {
            PatternConfidence = x.PatternConfidence, PatternType = x.PatternType, Direction = x.Direction, Timeframe = x.Timeframe,
            NormalizedLiquidity = x.NormalizedLiquidity, NormalizedVolatility = x.NormalizedVolatility, HourSin = x.HourSin, HourCos = x.HourCos, Label = x.Label,
            GrossReturnBps = x.GrossReturnBps
        };

        private static NumericalModelSnapshot ToSnapshot(NumericalModelRecord x) => new(x.Id, x.ModelVersion, x.Status, x.EvaluationRunId, x.InstrumentId, x.StrategyId,
            x.HorizonSeconds, x.Algorithm, x.ModelVersionTag, x.FeatureVersion, x.PatternVersion, x.LabelVersion, x.ProbabilityThreshold,
            Deserialize<NumericalModelMetrics>(x.BaselineWalkForwardJson), Deserialize<NumericalModelMetrics>(x.CandidateWalkForwardJson),
            Deserialize<NumericalModelMetrics>(x.BaselineHoldoutJson), Deserialize<NumericalModelMetrics>(x.CandidateHoldoutJson), x.ApprovalReady,
            Deserialize<string[]>(x.ApprovalReasonsJson), x.InputSha256, x.OutputSha256, x.CreatedAtUtc, x.DecidedAtUtc);
        internal static string OutputHash(NumericalModelRecord x) => Hash($"{x.InputSha256}|{x.ModelVersionTag}|{x.FeatureVersion}|{x.PatternVersion}|{x.LabelVersion}|{x.ProbabilityThreshold.ToString(CultureInfo.InvariantCulture)}|{x.BaselineWalkForwardJson}|{x.CandidateWalkForwardJson}|{x.BaselineHoldoutJson}|{x.CandidateHoldoutJson}|{Convert.ToHexString(SHA256.HashData(x.ModelArtifact))}");
        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
        private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new InvalidDataException($"Persisted {typeof(T).Name} payload is invalid.");
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        private static DateTime xDate(DateTime value) => ToUtc(value);
        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private sealed class ModelObservation
        {
            public long CandidateId { get; set; }
            public string CandidateKey { get; set; } = string.Empty;
            public string LabelInputSha256 { get; set; } = string.Empty;
            public DateTime EvaluatedAtUtc { get; set; }
            public DateTime LabelWindowEndUtc { get; set; }
            public float PatternConfidence { get; set; }
            public float PatternType { get; set; }
            public float Direction { get; set; }
            public float Timeframe { get; set; }
            public float NormalizedLiquidity { get; set; }
            public float NormalizedVolatility { get; set; }
            public float HourSin { get; set; }
            public float HourCos { get; set; }
            public decimal NetReturnBps { get; set; }
            public float GrossReturnBps { get; set; }
            public bool Label { get; set; }
        }
        private sealed class ModelRow
        {
            public float GrossReturnBps { get; set; }
            public float PatternConfidence { get; set; }
            public float PatternType { get; set; }
            public float Direction { get; set; }
            public float Timeframe { get; set; }
            public float NormalizedLiquidity { get; set; }
            public float NormalizedVolatility { get; set; }
            public float HourSin { get; set; }
            public float HourCos { get; set; }
            public bool Label { get; set; }
        }
        private sealed class ModelPrediction
        {
            [ColumnName("Probability")] public float Probability { get; set; }
            [ColumnName("ExpectedGrossReturnBps")] public float ExpectedGrossReturnBps { get; set; }
        }
        private sealed record ScoredObservation(ModelObservation Observation, decimal Probability, decimal? ExpectedGross);

        private sealed class CachedModel(NumericalModelRecord record, MLContext ml, ITransformer model,
            decimal historicalSelectedNetReturnBps, DateTime refreshAfterUtc, long epoch)
        {
            public NumericalModelRecord Record { get; } = record;
            public MLContext Ml { get; } = ml;
            public ITransformer Model { get; } = model;
            public decimal HistoricalSelectedNetReturnBps { get; } = historicalSelectedNetReturnBps;
            public long Epoch { get; } = epoch;
            private long _refreshAfterTicks = refreshAfterUtc.Ticks;
            public DateTime RefreshAfterUtc => new(Interlocked.Read(ref _refreshAfterTicks), DateTimeKind.Utc);
            public void Extend(DateTime untilUtc) => Interlocked.Exchange(ref _refreshAfterTicks, untilUtc.Ticks);
            public ConcurrentBag<PredictionEngine<ModelRow, ModelPrediction>> Engines { get; } = new();
        }
    }
}
