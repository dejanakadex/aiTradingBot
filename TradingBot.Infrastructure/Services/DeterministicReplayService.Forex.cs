using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.DTOs;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

public sealed partial class DeterministicReplayService
{
    private readonly ForexResearchContract? _forexContract;
    private readonly ForexTradeSimulator? _forexSimulator;
    private readonly ForexSignalSettings _forexSignals;
    private readonly ForexPatternSettings _forexPatterns;

    public async Task<ReplayBacktestSnapshot> RunForexResearchAsync(ReplayStartRequest request, CancellationToken cancellationToken = default)
    {
        if (!_settings.Enabled || _forexContract == null || _forexSimulator == null || _candidateResearchService == null)
            throw new InvalidOperationException("FX replay dependencies or replay permission are unavailable.");
        var from = ToUtc(request.FromUtc); var to = ToUtc(request.ToUtc);
        if (from >= to) throw new ArgumentException("FX replay range must be positive.");
        if (request.ForexBatchSize is < 1 or > 16384) throw new ArgumentException("FX processing batch size must be between 1 and 16,384.");
        request = request with { FromUtc = from, ToUtc = to };
        var coverageEvidence = ForexReplayCoveragePolicy.FreezeEvidence(request.ForexCoverage, request.InstrumentId, from, to);
        var boundCoverage = await _datasetStore.GetBoundForexCoverageAsync(cancellationToken);
        if (boundCoverage != null)
        {
            boundCoverage = ForexReplayCoveragePolicy.FreezeEvidence(boundCoverage, request.InstrumentId, from, to);
            if (coverageEvidence != null && JsonSerializer.Serialize(coverageEvidence) != JsonSerializer.Serialize(boundCoverage))
                throw new InvalidDataException("Derived FX dataset requires its bound coverage; an override cannot discard or replace gaps.");
            coverageEvidence = boundCoverage;
            request = request with { ForexCoverage = boundCoverage };
        }
        var contract = _forexContract.Get(request.InstrumentId, request.StrategyId);
        var p = contract.Profile;
        var instrument = _tradingSettings.GetConfiguredInstruments().Single(x => x.InstrumentId == p.InstrumentId);
        var verification = await _datasetStore.VerifyAsync(cancellationToken);
        if (!verification.IsValid) throw new InvalidDataException("Dataset integrity verification failed.");
        var manifest = await _datasetStore.GetManifestAsync(cancellationToken);
        using var index = new ForexReplayIndex();
        await index.LoadAsync(_datasetStore, request, instrument.Symbol, _shortAggregateSettings, cancellationToken);
        var after = await _datasetStore.VerifyAsync(cancellationToken);
        if (!after.IsValid || after.ManifestSha256 != verification.ManifestSha256)
            throw new InvalidDataException("Dataset changed while building replay index. Use a frozen snapshot.");
        var inputHash = ForexResearchContract.Hash(ForexReplayIndex.Version + $"|{request.InstrumentId}|{from:O}|{to:O}|" + verification.ManifestSha256 + "|"
            + index.CanonicalSha256 + "|" + ForexReplayCoveragePolicy.Version
            + "|" + JsonSerializer.Serialize(coverageEvidence));
        var configHash = ForexResearchContract.Hash(contract.Label + contract.Feature + contract.Pattern);
        var latest = new LatestMarketDataService(Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings), Microsoft.Extensions.Options.Options.Create(_tradingSettings));
        var inputs = new ForexFeatureService(latest, _featureEngine, Microsoft.Extensions.Options.Options.Create(_tradingSettings), Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings));
        var signalSettings = new ForexSignalSettings { Profiles = _forexSignals.Profiles.Where(x => x.Selection.InstrumentId == p.InstrumentId
            && x.Selection.StrategyId == p.StrategyId && x.Selection.IntervalSeconds == p.IntervalSeconds).ToArray() };
        if (signalSettings.Profiles.Length != 1 || !signalSettings.Profiles[0].Selection.Enabled)
            throw new InvalidOperationException("An enabled FX signal profile is required for research replay.");
        var trigger = new ForexSignalTriggerService(Microsoft.Extensions.Options.Options.Create(signalSettings), Microsoft.Extensions.Options.Options.Create(_forexPatterns), Microsoft.Extensions.Options.Options.Create(_tradingSettings),
            Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings), latest, inputs, _featureEngine, _candidateResearchService, _dbFactory, NullLoggerFactory.Instance);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (request.ForexResumeRunId.HasValue)
        {
            var previous = await db.ReplayRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ForexResumeRunId, cancellationToken)
                ?? throw new InvalidOperationException("FX recovery run does not exist.");
            if (previous.Status is not (ReplayRunStatus.Paused or ReplayRunStatus.Faulted or ReplayRunStatus.Cancelled)
                || previous.InputSha256 != inputHash || previous.StrategyVersion != contract.Label
                || previous.FeatureVersion != contract.Feature || previous.PatternVersion != contract.Pattern
                || previous.FromUtc != from || previous.ToUtc != to || previous.InstrumentId != p.InstrumentId || previous.StrategyId != p.StrategyId)
                throw new InvalidOperationException("FX recovery requires an incomplete run with the identical frozen dataset, range and research contract.");
        }
        var evidence = new ForexReplayDatasetEvidence(ForexReplayIndex.Version, verification.ManifestSha256, inputHash,
            index.EventCount, index.DuplicateEvents, request.ForexBatchSize, index.MaximumBufferedEvents,
            ParquetMarketDatasetStore.MaximumStreamingRowGroupRows,
            request.ForexResumeRunId, request.ForexResumeRunId.HasValue ? "DeterministicRebuildInNewAttempt" : "ContinuousStream");
        var run = new ReplayRunRecord { Id = Guid.NewGuid(), InstrumentId = p.InstrumentId, StrategyId = p.StrategyId, Symbol = instrument.Symbol,
            FromUtc = from, ToUtc = to, Status = ReplayRunStatus.Paused, InputSha256 = inputHash, InputEventCount = index.EventCount,
            CreatedAtUtc = _clock.UtcNow, StartedAtUtc = _clock.UtcNow, UpdatedAtUtc = _clock.UtcNow, FeatureVersion = contract.Feature,
            PatternVersion = contract.Pattern, StrategyVersion = contract.Label, MarketDataVersion = ForexReplayIndex.Version, Version = 1 };
        var databasePath = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        var evidenceDirectory = databasePath + ".fx-manifests";
        Directory.CreateDirectory(evidenceDirectory);
        await using (var output = new FileStream(Path.Combine(evidenceDirectory, run.Id.ToString("N") + ".json"), FileMode.CreateNew, FileAccess.Write))
            await JsonSerializer.SerializeAsync(output, new { runId = run.Id, fromUtc = from, toUtc = to, evidence,
                contract.Feature, contract.Pattern, contract.Label, coverageEvidence, manifest }, JsonOptions, cancellationToken);
        db.ReplayRunRecords.Add(run); await db.SaveChangesAsync(cancellationToken);
        try
        {
            var deadlines = new SortedSet<DateTime>();
            async Task Capture(DateTime now)
            {
                latest.AdvanceTo(now);
                var frame = inputs.Compute(p.InstrumentId, p.IntervalSeconds, now);
                if (frame?.PriceFeaturesReady == true && frame.Atr is > 0) index.AddAtr(new(now, frame.Atr.Value));
                await trigger.ProcessAsync(p.InstrumentId, now, cancellationToken, run.Id);
            }
            foreach (var batch in index.ReadEventBatches(request.ForexBatchSize, cancellationToken))
            {
                foreach (var record in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    while (deadlines.Count > 0 && deadlines.Min <= record.ReceivedTimeUtc)
                    { var at = deadlines.Min; deadlines.Remove(at); await Capture(at); }
                    latest.Apply(new() { EventId = record.EventId, InstrumentId = record.InstrumentId, Symbol = record.Symbol,
                        Kind = record.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
                        EventTimeUtc = record.EventTimeUtc, ReceivedTimeUtc = record.ReceivedTimeUtc, Source = record.Source,
                        IsFinal = true, Price = record.Price, Size = record.Size });
                    var width = TimeSpan.FromSeconds(p.IntervalSeconds).Ticks;
                    var deadline = new DateTime(record.EventTimeUtc.Ticks - record.EventTimeUtc.Ticks % width, DateTimeKind.Utc)
                        .AddSeconds(p.IntervalSeconds + _shortAggregateSettings.AllowedLatenessSeconds);
                    if (deadline > record.ReceivedTimeUtc) deadlines.Add(deadline);
                }
                run.ProcessedEventCount += batch.Count; run.CheckpointEventId = batch[^1].EventId;
                run.CheckpointReceivedTimeUtc = batch[^1].ReceivedTimeUtc; run.CheckpointEventTimeUtc = batch[^1].EventTimeUtc;
                run.UpdatedAtUtc = _clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            while (deadlines.Count > 0 && deadlines.Min <= to) { var at = deadlines.Min; deadlines.Remove(at); await Capture(at); }
            var candidateQuery = db.ResearchCandidateRecords.Where(x => x.ReplayRunId == run.Id && x.Outcome != ResearchCandidateOutcome.Rejected);
            // Bound result/report memory independently of raw tick count. Evaluator already has a 250k-observation bound.
            if (await candidateQuery.LongCountAsync(cancellationToken) > 250000)
                throw new InvalidOperationException("FX research exceeds 250,000 non-rejected candidates; narrow the research range.");
            var trades = new List<ReplayTradeResult>();
            var occupied = from; var equity = p.StartingEquityUsd; var dailyLoss = 0m; var day = from.Date;
            for (var offset = 0; ; offset += 512)
            {
                var candidates = await candidateQuery.OrderBy(x => x.EvaluatedAtUtc).ThenBy(x => x.CandidateKey).Skip(offset).Take(512).ToArrayAsync(cancellationToken);
                if (candidates.Length == 0) break;
                foreach (var candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var at = ToUtc(candidate.EvaluatedAtUtc);
                    var id = candidate.SignalId ?? Guid.Empty;
                    var windowEnd = ForexTradeSimulator.WindowEnd(at, p);
                    var quotes = index.Quotes(at, windowEnd, cancellationToken);
                    var atr = index.Atr(at.AddSeconds(-contract.Costs.MaximumSnapshotAgeSeconds), windowEnd);
                    var label = _forexSimulator.Simulate(id, candidate.Direction, at, quotes, atr, p, contract.Costs, p.StartingEquityUsd,
                        coverageEvidence: coverageEvidence);
                    candidate.LabelVersion = contract.Label;
                    db.CandidateLabelRecords.Add(new() { ResearchCandidateId = candidate.Id, HorizonSeconds = p.HoldingSeconds,
                        Status = label.Trade.Status == "Closed" ? CandidateLabelStatus.Complete : CandidateLabelStatus.InsufficientData,
                        WindowStartUtc = at, WindowEndUtc = label.LabelWindowEndUtc, EntryPrice = label.Trade.EntryPrice ?? 0,
                        ExitPrice = label.Trade.ExitPrice, NetReturnBps = label.NetReturnBps, GrossReturnBps = label.GrossReturnBps,
                        EstimatedCostBps = label.CostBps, MaximumFavorableExcursionBps = label.MfeBps, MaximumAdverseExcursionBps = label.MaeBps,
                        HorizonGrossReturnBps = label.Trade.HorizonReturnBps, LabelVersion = contract.Label,
                        InputSha256 = ForexResearchContract.Hash(inputHash + contract.Label + label.InputHash), CalculatedAtUtc = _clock.UtcNow,
                        TargetStopOutcome = label.Trade.ExitReason == "Target" ? TargetStopOutcome.TargetFirst : label.Trade.ExitReason == "Stop" ? TargetStopOutcome.StopFirst : TargetStopOutcome.Neither,
                        ReasonsJson = JsonSerializer.Serialize(new[] { label.Trade.ExitReason, "Historical execution scenario; no actual fill proof.", JsonSerializer.Serialize(label) }, JsonOptions) });
                    if (candidate.Outcome != ResearchCandidateOutcome.Accepted) continue;
                    db.ReplaySignalRecords.Add(new() { ReplayRunId = run.Id, SignalId = id, SourceEventId = candidate.SourceEventId,
                        PatternType = candidate.PatternType, Direction = candidate.Direction, Symbol = candidate.Symbol, Timeframe = candidate.Timeframe,
                        DetectedAtUtc = at, Confidence = candidate.Confidence, MetadataJson = candidate.MetadataJson, CreatedAtUtc = _clock.UtcNow });
                    if (at < occupied) { trades.Add(label.Trade with { Status = "Skipped", ExitReason = "OverlappingPosition", FilledQuantity = 0, ExitedQuantity = 0,
                        EntryTimeUtc = null, ExitTimeUtc = null, EntryPrice = null, ExitPrice = null, HorizonReturnBps = null, GrossPnl = null, NetPnl = null,
                        Commission = null, CoverageIssue = null }); continue; }
                    if (at.Date != day) { day = at.Date; dailyLoss = 0; }
                    var trade = _forexSimulator.Simulate(id, candidate.Direction, at, quotes, atr, p, contract.Costs, equity, dailyLoss, coverageEvidence).Trade;
                    trades.Add(trade);
                    if (trade.FilledQuantity > 0) occupied = trade.Status == "Closed" ? trade.ExitTimeUtc!.Value : to;
                    if (trade.NetPnl.HasValue) { equity += trade.NetPnl.Value; dailyLoss += Math.Min(0, trade.NetPnl.Value); }
                }
                await db.SaveChangesAsync(cancellationToken);
                db.ChangeTracker.Clear();
            }
            run.Status = ReplayRunStatus.Completed; run.CompletedAtUtc = _clock.UtcNow; run.UpdatedAtUtc = _clock.UtcNow;
            run.ProcessedEventCount = index.EventCount; run.SignalCount = trades.Count;
            run.OutputSha256 = await ComputeOutputHashAsync(db, run.Id, cancellationToken);
            db.Update(run);
            var resultsJson = JsonSerializer.Serialize(trades, JsonOptions);
            var backtest = new ReplayBacktestRecord { Id = Guid.NewGuid(), ReplayRunId = run.Id, InputSha256 = inputHash,
                RequestSha256 = configHash, OutputSha256 = ForexResearchContract.Hash(inputHash + configHash + run.OutputSha256 + resultsJson),
                ResultsJson = resultsJson, SignalCount = trades.Count, FilledTradeCount = trades.Count(x => x.FilledQuantity > 0),
                ClosedTradeCount = trades.Count(x => x.Status == "Closed"), RealizedNetPnl = trades.Sum(x => x.NetPnl ?? 0m), CreatedAtUtc = _clock.UtcNow };
            db.ReplayBacktestRecords.Add(backtest); await db.SaveChangesAsync(cancellationToken);
            return new(backtest.Id, run.Id, inputHash, configHash, backtest.OutputSha256, trades.Count, backtest.FilledTradeCount,
                backtest.ClosedTradeCount, backtest.RealizedNetPnl, backtest.CreatedAtUtc, trades)
                { ForexCoverage = coverageEvidence, ForexDataset = evidence };
        }
        catch
        {
            db.ChangeTracker.Clear();
            var failed = await db.ReplayRunRecords.FindAsync([run.Id], CancellationToken.None);
            failed!.Status = ReplayRunStatus.Faulted;
            failed.LastError = $"FX research replay failed; partial outputs must not train models. Recover with fx-replay --resume-run-id {run.Id:D} and identical frozen input; recovery creates a new attempt.";
            await db.SaveChangesAsync(CancellationToken.None); throw;
        }
    }
}
