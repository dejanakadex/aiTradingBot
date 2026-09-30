using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Trainers.LightGbm;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services
{
    public sealed class NumericalModelService : INumericalModelService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
        private readonly NumericalModelSettings _settings;
        private readonly IClock _clock;

        public NumericalModelService(IDbContextFactory<TradingBotDbContext> dbFactory, IOptions<NumericalModelSettings> settings, IClock clock)
        {
            _dbFactory = dbFactory;
            _settings = settings.Value;
            _clock = clock;
        }

        public async Task<NumericalModelSnapshot> TrainAsync(NumericalModelTrainingRequest request, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var evaluation = await db.ResearchEvaluationRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.EvaluationRunId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Research evaluation '{request.EvaluationRunId}' was not found.");
            var existing = await db.NumericalModelRecords.AsNoTracking().SingleOrDefaultAsync(x => x.EvaluationRunId == evaluation.Id, cancellationToken).ConfigureAwait(false);
            if (existing != null) return ToSnapshot(existing);

            var query = db.CandidateLabelRecords.AsNoTracking()
                .Where(x => x.HorizonSeconds == evaluation.HorizonSeconds
                    && (x.Status == CandidateLabelStatus.Complete || x.Status == CandidateLabelStatus.Ambiguous)
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
                    x.ResearchCandidate.NormalizedLiquidity, x.ResearchCandidate.NormalizedVolatility, NetReturnBps = x.NetReturnBps!.Value
                }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var rows = sourceRows.Select(x => new ModelObservation
            {
                CandidateId = x.ResearchCandidateId, CandidateKey = x.CandidateKey, EvaluatedAtUtc = x.EvaluatedAtUtc, LabelWindowEndUtc = x.LabelWindowEndUtc,
                PatternConfidence = (float)x.Confidence, PatternType = (float)x.PatternType, Direction = (float)x.Direction, Timeframe = (float)x.Timeframe,
                NormalizedLiquidity = (float)(x.NormalizedLiquidity ?? 0m), NormalizedVolatility = (float)(x.NormalizedVolatility ?? 0m),
                HourSin = (float)Math.Sin(2d * Math.PI * x.EvaluatedAtUtc.Hour / 24d), HourCos = (float)Math.Cos(2d * Math.PI * x.EvaluatedAtUtc.Hour / 24d),
                NetReturnBps = x.NetReturnBps, Label = x.NetReturnBps > 0m
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
            foreach (var fold in folds.Where(x => x.SelectedConfidenceThreshold.HasValue))
            {
                var training = rows.Where(x => x.EvaluatedAtUtc >= xDate(fold.TrainingFromUtc) && x.LabelWindowEndUtc <= xDate(fold.TrainingToUtc)).ToArray();
                var validation = rows.Where(x => x.EvaluatedAtUtc >= xDate(fold.ValidationFromUtc) && x.LabelWindowEndUtc <= xDate(fold.ValidationToUtc)).ToArray();
                if (training.Length < _settings.MinimumTrainingSamples || validation.Length == 0) continue;
                baselineWfSelected.AddRange(validation.Where(x => x.PatternConfidence >= (float)fold.SelectedConfidenceThreshold!.Value));
                var (model, _) = Train(training);
                var trainingScores = Score(model, training);
                var threshold = SelectThreshold(trainingScores);
                candidateWfSelected.AddRange(Score(model, validation).Where(x => x.Probability >= threshold).Select(x => x.Observation));
            }
            if (candidateWfSelected.Count < _settings.MinimumValidationSelections)
                throw new InvalidOperationException("Walk-forward folds did not produce enough candidate model selections.");

            var (finalModel, finalSchema) = Train(preHoldout);
            var probabilityThreshold = SelectThreshold(Score(finalModel, preHoldout));
            var candidateHoldoutSelected = Score(finalModel, holdout).Where(x => x.Probability >= probabilityThreshold).Select(x => x.Observation).ToArray();
            var baselineHoldoutSelected = holdout.Where(x => x.PatternConfidence >= (float)evaluation.SelectedConfidenceThreshold).ToArray();
            var baselineWf = Metrics(baselineWfSelected);
            var candidateWf = Metrics(candidateWfSelected);
            var baselineHoldout = Metrics(baselineHoldoutSelected);
            var candidateHoldout = Metrics(candidateHoldoutSelected);
            var approvalReasons = ApprovalReasons(baselineWf, candidateWf, baselineHoldout, candidateHoldout);

            using var artifact = new MemoryStream();
            new MLContext(seed: _settings.RandomSeed).Model.Save(finalModel, finalSchema, artifact);
            var artifactBytes = artifact.ToArray();
            var inputHash = Hash(string.Join('\n', rows.Select(x => $"{x.CandidateId}|{x.CandidateKey}|{x.EvaluatedAtUtc:O}|{x.PatternConfidence:R}|{x.NormalizedLiquidity:R}|{x.NormalizedVolatility:R}|{x.NetReturnBps.ToString(CultureInfo.InvariantCulture)}")));
            var version = await db.NumericalModelRecords.Where(x => x.InstrumentId == evaluation.InstrumentId && x.StrategyId == evaluation.StrategyId)
                .Select(x => (int?)x.ModelVersion).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0;
            var record = new NumericalModelRecord
            {
                Id = Guid.NewGuid(), ModelVersion = version + 1, Status = NumericalModelStatus.Draft, EvaluationRunId = evaluation.Id,
                InstrumentId = evaluation.InstrumentId, StrategyId = evaluation.StrategyId, HorizonSeconds = evaluation.HorizonSeconds,
                Algorithm = "ML.NET LightGBM binary classification", ModelVersionTag = "numerical-model-v1",
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
            if (string.IsNullOrWhiteSpace(request.Reviewer) || request.Reason?.Trim().Length < 10) throw new ArgumentException("Reviewer and a decision reason of at least 10 characters are required.");
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.NumericalModelRecords.SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Numerical model '{id}' was not found.");
            if (record.Status != NumericalModelStatus.Draft) throw new InvalidOperationException("Only a Draft model can receive a decision.");
            if (!string.Equals(record.OutputSha256, request.ExpectedOutputSha256, StringComparison.Ordinal) || record.OutputSha256 != OutputHash(record))
                throw new InvalidDataException("Model output hash does not match the reviewed artifact.");
            if (request.Approve && !record.ApprovalReady) throw new InvalidOperationException($"Model did not beat baseline gates: {string.Join(" ", Deserialize<string[]>(record.ApprovalReasonsJson))}");
            if (request.Approve)
            {
                var previous = await db.NumericalModelRecords.Where(x => x.Id != id && x.InstrumentId == record.InstrumentId && x.StrategyId == record.StrategyId && x.Status == NumericalModelStatus.Approved).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var item in previous) item.Status = NumericalModelStatus.Superseded;
            }
            record.Status = request.Approve ? NumericalModelStatus.Approved : NumericalModelStatus.Rejected;
            record.Reviewer = request.Reviewer.Trim(); record.DecisionReason = request.Reason.Trim(); record.DecidedAtUtc = ToUtc(_clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToSnapshot(record);
        }

        public async Task<NumericalModelPrediction> PredictAsync(NumericalModelPredictionRequest request, CancellationToken cancellationToken = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var record = await db.NumericalModelRecords.AsNoTracking().Where(x => x.Status == NumericalModelStatus.Approved && x.InstrumentId == request.InstrumentId && x.StrategyId == request.StrategyId)
                .OrderByDescending(x => x.ModelVersion).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No manually approved numerical model matches the instrument and strategy.");
            if (record.OutputSha256 != OutputHash(record)) throw new InvalidDataException("Approved model artifact failed hash verification.");
            if (record.FeatureVersion != request.FeatureVersion) throw new InvalidOperationException("Runtime feature version does not match the approved model.");
            var ml = new MLContext(seed: _settings.RandomSeed);
            using var stream = new MemoryStream(record.ModelArtifact, writable: false);
            var model = ml.Model.Load(stream, out _);
            var engine = ml.Model.CreatePredictionEngine<ModelObservation, ModelPrediction>(model);
            var row = FromRequest(request);
            var probability = (decimal)engine.Predict(row).Probability;
            var eligible = probability >= record.ProbabilityThreshold;
            return new NumericalModelPrediction(record.Id, record.ModelVersion, probability, record.ProbabilityThreshold, eligible,
                eligible ? "Approved local model probability passed the threshold." : "Local model probability is below the approved threshold.");
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

        private (ITransformer Model, DataViewSchema Schema) Train(ModelObservation[] rows)
        {
            var ml = new MLContext(seed: _settings.RandomSeed);
            var data = ml.Data.LoadFromEnumerable(rows);
            var pipeline = ml.Transforms.Concatenate("Features", nameof(ModelObservation.PatternConfidence), nameof(ModelObservation.PatternType), nameof(ModelObservation.Direction), nameof(ModelObservation.Timeframe), nameof(ModelObservation.NormalizedLiquidity), nameof(ModelObservation.NormalizedVolatility), nameof(ModelObservation.HourSin), nameof(ModelObservation.HourCos))
                .Append(ml.BinaryClassification.Trainers.LightGbm(new LightGbmBinaryTrainer.Options
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
            var data = ml.Data.LoadFromEnumerable(rows);
            var predictions = ml.Data.CreateEnumerable<ModelPrediction>(model.Transform(data), reuseRowObject: false).ToArray();
            return rows.Zip(predictions, (observation, prediction) => new ScoredObservation(observation, (decimal)prediction.Probability)).ToArray();
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

        private static ModelObservation FromRequest(NumericalModelPredictionRequest x) => new()
        {
            PatternConfidence = (float)x.PatternConfidence, PatternType = x.PatternType, Direction = x.Direction, Timeframe = x.Timeframe,
            NormalizedLiquidity = (float)x.NormalizedLiquidity, NormalizedVolatility = (float)x.NormalizedVolatility,
            HourSin = (float)Math.Sin(2d * Math.PI * x.ObservedAtUtc.Hour / 24d), HourCos = (float)Math.Cos(2d * Math.PI * x.ObservedAtUtc.Hour / 24d)
        };

        private static NumericalModelSnapshot ToSnapshot(NumericalModelRecord x) => new(x.Id, x.ModelVersion, x.Status, x.EvaluationRunId, x.InstrumentId, x.StrategyId,
            x.HorizonSeconds, x.Algorithm, x.ModelVersionTag, x.FeatureVersion, x.PatternVersion, x.LabelVersion, x.ProbabilityThreshold,
            Deserialize<NumericalModelMetrics>(x.BaselineWalkForwardJson), Deserialize<NumericalModelMetrics>(x.CandidateWalkForwardJson),
            Deserialize<NumericalModelMetrics>(x.BaselineHoldoutJson), Deserialize<NumericalModelMetrics>(x.CandidateHoldoutJson), x.ApprovalReady,
            Deserialize<string[]>(x.ApprovalReasonsJson), x.InputSha256, x.OutputSha256, x.CreatedAtUtc, x.DecidedAtUtc);
        private static string OutputHash(NumericalModelRecord x) => Hash($"{x.InputSha256}|{x.ModelVersionTag}|{x.ProbabilityThreshold.ToString(CultureInfo.InvariantCulture)}|{x.BaselineWalkForwardJson}|{x.CandidateWalkForwardJson}|{x.BaselineHoldoutJson}|{x.CandidateHoldoutJson}|{Convert.ToHexString(SHA256.HashData(x.ModelArtifact))}");
        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
        private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ?? throw new InvalidDataException($"Persisted {typeof(T).Name} payload is invalid.");
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        private static DateTime xDate(DateTime value) => ToUtc(value);
        private static DateTime ToUtc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        private sealed class ModelObservation
        {
            public long CandidateId { get; set; }
            public string CandidateKey { get; set; } = string.Empty;
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
            public bool Label { get; set; }
        }
        private sealed class ModelPrediction
        {
            [ColumnName("Probability")] public float Probability { get; set; }
        }
        private sealed record ScoredObservation(ModelObservation Observation, decimal Probability);
    }
}
