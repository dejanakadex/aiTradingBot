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
        if (from >= to || to - from > TimeSpan.FromDays(7)) throw new ArgumentException("FX replay range must be positive and at most seven days.");
        var contract = _forexContract.Get(request.InstrumentId, request.StrategyId);
        var p = contract.Profile;
        var instrument = _tradingSettings.GetConfiguredInstruments().Single(x => x.InstrumentId == p.InstrumentId);
        if (!(await _datasetStore.VerifyAsync(cancellationToken)).IsValid) throw new InvalidDataException("Dataset integrity verification failed.");
        var records = (await _datasetStore.ReadAsync(new(request.InstrumentId, from, to, ["bid", "ask"]), cancellationToken))
            .Where(x => x.ReceivedTimeUtc <= to).OrderBy(x => x.ReceivedTimeUtc).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
        if (records.Length > 200000) throw new InvalidDataException("FX research input exceeds 200,000 events; select a smaller range.");
        var quotes = ForexTradeSimulator.ReadQuotes(records, instrument.InstrumentId, instrument.Symbol);
        var inputHash = ComputeInputHash(records);
        var configHash = ForexResearchContract.Hash(contract.Label + contract.Feature + contract.Pattern);
        var latest = new LatestMarketDataService(Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings), Microsoft.Extensions.Options.Options.Create(_tradingSettings));
        var inputs = new ForexFeatureService(latest, _featureEngine, Microsoft.Extensions.Options.Options.Create(_tradingSettings), Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings));
        var signalSettings = new ForexSignalSettings { Profiles = _forexSignals.Profiles.Where(x => x.Selection.InstrumentId == p.InstrumentId
            && x.Selection.StrategyId == p.StrategyId && x.Selection.IntervalSeconds == p.IntervalSeconds).ToArray() };
        if (signalSettings.Profiles.Length != 1 || !signalSettings.Profiles[0].Selection.Enabled)
            throw new InvalidOperationException("An enabled FX signal profile is required for research replay.");
        var trigger = new ForexSignalTriggerService(Microsoft.Extensions.Options.Options.Create(signalSettings), Microsoft.Extensions.Options.Options.Create(_forexPatterns), Microsoft.Extensions.Options.Options.Create(_tradingSettings),
            Microsoft.Extensions.Options.Options.Create(_shortAggregateSettings), latest, inputs, _featureEngine, _candidateResearchService, _dbFactory, NullLoggerFactory.Instance);
        var run = new ReplayRunRecord { Id = Guid.NewGuid(), InstrumentId = p.InstrumentId, StrategyId = p.StrategyId, Symbol = instrument.Symbol,
            FromUtc = from, ToUtc = to, Status = ReplayRunStatus.Paused, InputSha256 = inputHash, InputEventCount = records.Length,
            CreatedAtUtc = _clock.UtcNow, StartedAtUtc = _clock.UtcNow, UpdatedAtUtc = _clock.UtcNow, FeatureVersion = contract.Feature,
            PatternVersion = contract.Pattern, StrategyVersion = contract.Label, MarketDataVersion = ForexQuoteAggregator.QuoteSource, Version = 1 };
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.ReplayRunRecords.Add(run); await db.SaveChangesAsync(cancellationToken);
        try
        {
            var atr = new List<ForexReplayAtr>();
            var deadlines = new SortedSet<DateTime>();
            async Task Capture(DateTime now)
            {
                latest.AdvanceTo(now);
                var frame = inputs.Compute(p.InstrumentId, p.IntervalSeconds, now);
                if (frame?.PriceFeaturesReady == true && frame.Atr is > 0) atr.Add(new(now, frame.Atr.Value));
                await trigger.ProcessAsync(p.InstrumentId, now, cancellationToken, run.Id);
            }
            foreach (var record in records)
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
            while (deadlines.Count > 0 && deadlines.Min <= to) { var at = deadlines.Min; deadlines.Remove(at); await Capture(at); }
            var candidates = await db.ResearchCandidateRecords.Where(x => x.ReplayRunId == run.Id)
                .OrderBy(x => x.EvaluatedAtUtc).ThenBy(x => x.CandidateKey).ToArrayAsync(cancellationToken);
            var trades = new List<ReplayTradeResult>();
            var occupied = from; var equity = p.StartingEquityUsd; var dailyLoss = 0m; var day = from.Date;
            foreach (var candidate in candidates.Where(x => x.Outcome != ResearchCandidateOutcome.Rejected))
            {
                var at = ToUtc(candidate.EvaluatedAtUtc);
                var id = candidate.SignalId ?? Guid.Empty;
                var label = _forexSimulator.Simulate(id, candidate.Direction, at, quotes, atr, p, contract.Costs, p.StartingEquityUsd);
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
                    EntryTimeUtc = null, ExitTimeUtc = null, EntryPrice = null, ExitPrice = null, HorizonReturnBps = null, GrossPnl = null, NetPnl = null, Commission = null }); continue; }
                if (at.Date != day) { day = at.Date; dailyLoss = 0; }
                var trade = _forexSimulator.Simulate(id, candidate.Direction, at, quotes, atr, p, contract.Costs, equity, dailyLoss).Trade;
                trades.Add(trade);
                if (trade.FilledQuantity > 0) occupied = trade.Status == "Closed" ? trade.ExitTimeUtc!.Value : to;
                if (trade.NetPnl.HasValue) { equity += trade.NetPnl.Value; dailyLoss += Math.Min(0, trade.NetPnl.Value); }
            }
            await db.SaveChangesAsync(cancellationToken);
            run.Status = ReplayRunStatus.Completed; run.CompletedAtUtc = _clock.UtcNow; run.UpdatedAtUtc = _clock.UtcNow;
            run.ProcessedEventCount = records.Length; run.SignalCount = trades.Count;
            run.OutputSha256 = await ComputeOutputHashAsync(db, run.Id, cancellationToken);
            var resultsJson = JsonSerializer.Serialize(trades, JsonOptions);
            var backtest = new ReplayBacktestRecord { Id = Guid.NewGuid(), ReplayRunId = run.Id, InputSha256 = inputHash,
                RequestSha256 = configHash, OutputSha256 = ForexResearchContract.Hash(inputHash + configHash + run.OutputSha256 + resultsJson),
                ResultsJson = resultsJson, SignalCount = trades.Count, FilledTradeCount = trades.Count(x => x.FilledQuantity > 0),
                ClosedTradeCount = trades.Count(x => x.Status == "Closed"), RealizedNetPnl = trades.Sum(x => x.NetPnl ?? 0m), CreatedAtUtc = _clock.UtcNow };
            db.ReplayBacktestRecords.Add(backtest); await db.SaveChangesAsync(cancellationToken);
            return new(backtest.Id, run.Id, inputHash, configHash, backtest.OutputSha256, trades.Count, backtest.FilledTradeCount,
                backtest.ClosedTradeCount, backtest.RealizedNetPnl, backtest.CreatedAtUtc, trades);
        }
        catch
        {
            db.ChangeTracker.Clear();
            var failed = await db.ReplayRunRecords.FindAsync([run.Id], CancellationToken.None);
            failed!.Status = ReplayRunStatus.Faulted; failed.LastError = "FX research replay failed; partial outputs must not train models.";
            await db.SaveChangesAsync(CancellationToken.None); throw;
        }
    }
}
