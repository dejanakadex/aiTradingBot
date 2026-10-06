using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Persistence;

namespace TradingBot.Infrastructure.Services;

public sealed class ReplayBacktestService : IReplayBacktestService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDbContextFactory<TradingBotDbContext> _dbFactory;
    private readonly IMarketDatasetStore _dataset;
    private readonly ReplayTradeSimulator _simulator;
    private readonly IClock _clock;

    public ReplayBacktestService(IDbContextFactory<TradingBotDbContext> dbFactory,
        IMarketDatasetStore dataset, ReplayTradeSimulator simulator, IClock clock)
    {
        _dbFactory = dbFactory;
        _dataset = dataset;
        _simulator = simulator;
        _clock = clock;
    }

    public async Task<ReplayBacktestSnapshot> RunAsync(Guid replayRunId, ReplayBacktestRequest request,
        CancellationToken cancellationToken = default)
    {
        ReplayTradeSimulator.Validate(request);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.ReplayRunRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == replayRunId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException($"Replay run {replayRunId} was not found.");
        if (run.Status != ReplayRunStatus.Completed)
            throw new InvalidOperationException("Backtest requires a completed replay run.");
        var verification = await _dataset.VerifyAsync(cancellationToken).ConfigureAwait(false);
        if (!verification.IsValid)
            throw new InvalidDataException($"Dataset integrity verification failed: {string.Join(" ", verification.Errors)}");
        var events = (await _dataset.ReadAsync(new DatasetQuery(run.InstrumentId, run.FromUtc, run.ToUtc,
            ["bar", "bid", "ask", "trade"]), cancellationToken).ConfigureAwait(false))
            .GroupBy(x => x.EventId, StringComparer.Ordinal)
            .Select(x => x.OrderBy(y => y.ReceivedTimeUtc).First())
            .OrderBy(x => x.EventTimeUtc).ThenBy(x => x.ReceivedTimeUtc).ThenBy(x => x.EventId, StringComparer.Ordinal)
            .ToArray();
        if (events.Length != run.InputEventCount
            || DeterministicReplayService.ComputeInputHash(events) != run.InputSha256)
            throw new InvalidDataException("Replay input changed after the signal run; backtest refused.");
        if (await DeterministicReplayService.ComputeOutputHashAsync(db, replayRunId, cancellationToken).ConfigureAwait(false)
            != run.OutputSha256)
            throw new InvalidDataException("Replay signals or research output changed after completion; backtest refused.");

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = Hash($"replay-backtest-v1|{_simulator.ConfigurationFingerprint}|{requestJson}");
        var existing = await db.ReplayBacktestRecords.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ReplayRunId == replayRunId && x.RequestSha256 == requestHash, cancellationToken)
            .ConfigureAwait(false);
        if (existing != null) return ToSnapshot(existing);
        var signals = await db.ReplaySignalRecords.AsNoTracking().Where(x => x.ReplayRunId == replayRunId)
            .OrderBy(x => x.DetectedAtUtc).ThenBy(x => x.SignalId)
            .Select(x => new ReplaySignalSnapshot(x.Id, x.ReplayRunId, x.SignalId, x.SourceEventId,
                x.PatternType, x.Direction, x.Symbol, x.Timeframe, x.DetectedAtUtc, x.Confidence,
                x.RelevantPriceLevelsJson, x.MetadataJson, x.FeaturesJson))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var trades = _simulator.Simulate(events, signals, request);
        var resultsJson = JsonSerializer.Serialize(trades, JsonOptions);
        var record = new ReplayBacktestRecord
        {
            Id = Guid.NewGuid(),
            ReplayRunId = replayRunId,
            InputSha256 = run.InputSha256,
            RequestSha256 = requestHash,
            OutputSha256 = Hash($"{run.InputSha256}|{requestHash}|{run.OutputSha256}|{resultsJson}"),
            ResultsJson = resultsJson,
            SignalCount = signals.Length,
            FilledTradeCount = trades.Count(x => x.FilledQuantity > 0m),
            ClosedTradeCount = trades.Count(x => x.Status == "Closed"),
            RealizedNetPnl = trades.Sum(x => x.NetPnl ?? 0m),
            CreatedAtUtc = _clock.UtcNow
        };
        db.ReplayBacktestRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return ToSnapshot(record);
    }

    public async Task<ReplayBacktestSnapshot?> GetAsync(Guid backtestId, CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var record = await db.ReplayBacktestRecords.AsNoTracking().SingleOrDefaultAsync(x => x.Id == backtestId, cancellationToken)
            .ConfigureAwait(false);
        return record == null ? null : ToSnapshot(record);
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ReplayBacktestSnapshot ToSnapshot(ReplayBacktestRecord record) => new(
        record.Id, record.ReplayRunId, record.InputSha256, record.RequestSha256, record.OutputSha256,
        record.SignalCount, record.FilledTradeCount, record.ClosedTradeCount, record.RealizedNetPnl,
        record.CreatedAtUtc, JsonSerializer.Deserialize<ReplayTradeResult[]>(record.ResultsJson, JsonOptions) ?? []);
}
