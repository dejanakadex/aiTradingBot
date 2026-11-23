using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

// Offline diagnostic: no application host, broker, operational database or approval mutations.
if (args.Length != 5) throw new ArgumentException("Usage: CONFIG SNAPSHOT FROM_UTC TO_UTC NEW_REPORT (absolute paths)");
foreach (var i in new[] { 0, 1, 4 }) if (!Path.IsPathFullyQualified(args[i])) throw new ArgumentException("Absolute paths required.");
if (File.Exists(args[4])) throw new ArgumentException("Report must be new.");
DateTime Utc(string x) { if (!x.EndsWith('Z')) throw new ArgumentException("UTC Z suffix required."); return DateTime.Parse(x, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal); }
var from = Utc(args[2]); var to = Utc(args[3]);
if (from >= to || to - from > TimeSpan.FromDays(1)) throw new ArgumentException("Positive range, at most one day.");
var config = new ConfigurationBuilder().AddJsonFile(args[0]).Build();
var trading = config.GetSection("TradingSettings").Get<TradingSettings>() ?? throw new ArgumentException("TradingSettings required.");
if (trading.Enabled || trading.OperatingMode != TradingOperatingMode.AnalysisOnly || trading.ForexPaperExecutionEnabled || trading.LiveTradingExplicitlyEnabled)
    throw new ArgumentException("Requires trading-disabled AnalysisOnly config.");
var instrument = trading.GetEnabledInstruments().Single(x => x.InstrumentType == InstrumentType.Forex);
var aggregates = config.GetSection("ShortAggregates").Get<ShortAggregateSettings>() ?? new();
aggregates.IntervalsSeconds = config.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>() ?? aggregates.IntervalsSeconds;
var patterns = config.GetSection("ForexPatterns").Get<ForexPatternSettings>() ?? new();
var signals = config.GetSection("ForexSignals").Get<ForexSignalSettings>() ?? new();
// Approved strategies need real persisted cooldown and acceptance coverage; this diagnostic explicitly excludes them.
if (!signals.Profiles.Any(x => x.Selection.Enabled) || signals.Profiles.Any(x => x.ApprovedSignalVersion != null || x.ApprovedPatternVersion != null || x.ApprovedFeatureVersion != null))
    throw new ArgumentException("Requires enabled research selection with no signal approvals.");
var clock = new AuditClock { UtcNow = from };
var store = new ParquetMarketDatasetStore(Options.Create(new DatasetStorageSettings { RootPath = args[1] }), clock, NullLogger<ParquetMarketDatasetStore>.Instance);
var verification = await store.VerifyAsync();
if (!verification.IsValid) throw new InvalidDataException("Snapshot verification failed.");
var manifest = await store.GetManifestAsync();
var bound = manifest.Files.Where(x => x.InstrumentId == instrument.InstrumentId && x.DataType is "bid" or "ask" && x.MaximumEventTimeUtc >= from && x.MinimumEventTimeUtc < to).Sum(x => (long)x.RowCount);
if (bound > 200_000) throw new ArgumentException("Select at most 200,000 quote rows.");
var rows = (await store.ReadAsync(new(instrument.InstrumentId, from, to, ["bid", "ask"])))
    .Where(x => x.EventTimeUtc >= from && x.EventTimeUtc < to && x.ReceivedTimeUtc < to)
    .OrderBy(x => x.ReceivedTimeUtc).ThenBy(x => x.EventTimeUtc).ThenBy(x => x.DataType == "bid" ? 0 : 1).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
if (rows.Length == 0 || rows.Select(x => x.EventId).Distinct().Count() != rows.Length) throw new InvalidDataException("Empty or duplicate input.");
using var strictDb = new MemoryDb(); using var bufferedDb = new MemoryDb();
using var strict = Quality(strictDb, 0); using var buffered = Quality(bufferedDb, 1000);
MarketDataQualityService Quality(MemoryDb db, int interval) => new(db, Options.Create(trading), clock,
    NullLogger<MarketDataQualityService>.Instance, Options.Create(aggregates),
    Options.Create(new MarketDataCollectionSettings { QualityStateCheckpointMilliseconds = interval }), new FrozenTime());
var left = new LatestMarketDataService(Options.Create(aggregates), Options.Create(trading));
var right = new LatestMarketDataService(Options.Create(aggregates), Options.Create(trading));
var engine = new FeatureEngine(config.GetSection("CanonicalFeatures").Get<CanonicalFeatureSettings>() ?? new());
var leftFeatures = new ForexFeatureService(left, engine, Options.Create(trading), Options.Create(aggregates));
var rightFeatures = new ForexFeatureService(right, engine, Options.Create(trading), Options.Create(aggregates));
using var leftSink = new RecordingSink(); using var rightSink = new RecordingSink();
ForexSignalTriggerService Trigger(LatestMarketDataService latest, ForexFeatureService features, MemoryDb db, RecordingSink sink) =>
    new(Options.Create(signals), Options.Create(patterns), Options.Create(trading), Options.Create(aggregates), latest, features, engine, sink, db, NullLoggerFactory.Instance);
var leftTrigger = Trigger(left, leftFeatures, strictDb, leftSink); var rightTrigger = Trigger(right, rightFeatures, bufferedDb, rightSink);
var replayId = Guid.Parse("df713e6b-857a-4b42-a6d0-e476ad558ec4");
using var featureHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
var completed = new Dictionary<int, DateTime>(); var frames = new Dictionary<int, int>();
var nextFlush = from; var count = 0; var healthy = 0; var warm = 0; var comparedBatches = 0;
void Equal<T>(T a, T b, string scope) { if (CanonicalJson.Serialize(a) != CanonicalJson.Serialize(b)) throw new InvalidDataException($"Parity mismatch: {scope}, event {count}."); }
async Task Evaluate(DateTime at)
{
    left.AdvanceTo(at); right.AdvanceTo(at);
    foreach (var interval in aggregates.IntervalsSeconds)
    {
        var a = left.GetCompletedForexAggregates(instrument.InstrumentId, interval, at);
        var b = right.GetCompletedForexAggregates(instrument.InstrumentId, interval, at);
        if (a.Count == 0) { Equal(a, b, "empty bars"); continue; }
        if (completed.GetValueOrDefault(interval) == a[^1].WindowEndUtc) continue;
        Equal(a, b, "bars");
        foreach (var bar in a.Where(x => x.WindowEndUtc > completed.GetValueOrDefault(interval)))
        {
            // Match the signal service: each newly completed bar uses its own availability time.
            var fa = leftFeatures.Compute(instrument.InstrumentId, interval, bar.AvailableAtUtc);
            var fb = rightFeatures.Compute(instrument.InstrumentId, interval, bar.AvailableAtUtc);
            Equal(fa, fb, "features"); frames[interval] = frames.GetValueOrDefault(interval) + 1;
            if (fa?.PriceFeaturesReady == true) warm++;
            featureHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { interval, bar.WindowEndUtc, bar.AvailableAtUtc, features = fa }) + "\n"));
        }
        completed[interval] = a[^1].WindowEndUtc;
    }
    Equal(await leftTrigger.ProcessAsync(instrument.InstrumentId, at, replayRunId: replayId),
        await rightTrigger.ProcessAsync(instrument.InstrumentId, at, replayRunId: replayId), "selected signals");
    Equal(leftSink.Batches, rightSink.Batches, "decision batch count");
    if (leftSink.Batches != comparedBatches)
    {
        Equal(leftSink.LastBatch, rightSink.LastBatch, "pattern/signal decisions");
        comparedBatches = leftSink.Batches;
    }
}
foreach (var row in rows)
{
    count++; clock.UtcNow = row.ReceivedTimeUtc;
    if (clock.UtcNow >= nextFlush) { await buffered.FlushCheckpointsAsync(); nextFlush = clock.UtcNow.AddSeconds(1); }
    var e = new CanonicalMarketDataEvent { EventId = row.EventId, InstrumentId = row.InstrumentId, Symbol = row.Symbol,
        Kind = row.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask, EventTimeUtc = row.EventTimeUtc,
        ReceivedTimeUtc = row.ReceivedTimeUtc, Source = row.Source, Sequence = row.Sequence, IsFinal = row.IsFinal,
        Timeframe = row.Timeframe, Price = row.Price, Size = row.Size };
    var a = await strict.EvaluateAsync(e); var b = await buffered.EvaluateAsync(e);
    Equal(a, b, "quality");
    if (a.Status.ToString() != row.QualityStatus) throw new InvalidDataException("Recorded/live quality differs from reconstruction.");
    if (a.IsHealthy) { healthy++; left.Apply(e); right.Apply(e); }
    // Same explicit receive clock on both paths. This is not a reconstruction of live timer scheduling.
    await Evaluate(clock.UtcNow);
    if (count % 25000 == 0) Console.WriteLine($"Compared {count} events.");
}
clock.UtcNow = to; await Evaluate(to); await buffered.StopAsync(CancellationToken.None);
Equal(await strict.GetStreamsAsync(), await buffered.GetStreamsAsync(), "final durable state");
var after = await store.VerifyAsync();
if (!after.IsValid || after.ManifestSha256 != verification.ManifestSha256) throw new InvalidDataException("Snapshot changed during parity check.");
var leftDigest = leftSink.Digest(); var rightDigest = rightSink.Digest(); Equal(leftDigest, rightDigest, "all decision hashes");
if (warm == 0 || leftSink.Batches == 0 || leftSink.Evaluations == 0) throw new InvalidDataException("Insufficient warmed features/decisions.");
var report = new { passed = true, canSendOrders = false, instrumentId = instrument.InstrumentId, fromUtc = from, toUtc = to,
    verification.ManifestSha256, configSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[0]))), events = count, healthy,
    frames, warmedFeatureFrames = warm, decisionBatches = leftSink.Batches, patternEvaluations = leftSink.Evaluations,
    patternCandidates = leftSink.Candidates, selectedSignals = leftSink.Selected, featureSha256 = Convert.ToHexString(featureHash.GetHashAndReset()),
    strictDecisionSha256 = leftDigest, checkpointDecisionSha256 = rightDigest,
    limitations = new[] { "Receive-ordered offline comparison, not live timer scheduling parity.", "Only random PipelineContext.CorrelationId is excluded; deterministic signal identity and all decision fields are compared.", "Current unapproved research profile: accepted signal/cooldown, model and order paths not validated.", "In-memory SQLite; storage timing/failure coverage comes from separate tests and live metrics." } };
await using var output = new FileStream(args[4], FileMode.CreateNew, FileAccess.Write);
await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine($"Parity passed: {count} events, {warm} warm frames, {leftSink.Evaluations} evaluations.");

sealed class FrozenTime : TimeProvider { public override long GetTimestamp() => 0; }
sealed class AuditClock : IClock { public DateTime UtcNow { get; set; } }
sealed class MemoryDb : IDbContextFactory<TradingBotDbContext>, IDisposable
{
    readonly SqliteConnection connection = new("Data Source=:memory:");
    public MemoryDb() { connection.Open(); using var db = CreateDbContext(); db.Database.EnsureCreated(); }
    public TradingBotDbContext CreateDbContext() => new(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
    public Task<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    public void Dispose() => connection.Dispose();
}
sealed class RecordingSink : ICandidateResearchService, IDisposable
{
    readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    public string LabelVersion => "parity-no-labels";
    public string? LastBatch; public int Batches, Evaluations, Candidates, Selected;
    public Task<int> PersistBatchAsync(PatternDetectionBatch batch, string sourceEventId, Guid? replayRunId = null, CandidateEvaluationContext? evaluationContext = null, CancellationToken cancellationToken = default)
    {
        LastBatch = CanonicalJson.Serialize(new { batch, sourceEventId, replayRunId, evaluationContext });
        hash.AppendData(Encoding.UTF8.GetBytes(LastBatch + "\n")); Batches++; Evaluations += batch.Evaluations.Count;
        Candidates += batch.Evaluations.Count(x => x.Candidate != null); Selected += batch.Candidates.Count;
        if (batch.Candidates.Count != 0) throw new InvalidOperationException("Unexpected approved signal; diagnostic excludes accepted/cooldown path.");
        return Task.FromResult(batch.Evaluations.Count);
    }
    public string Digest() => Convert.ToHexString(hash.GetHashAndReset());
    public void Dispose() => hash.Dispose();
    public Task MarkBlockedAsync(string candidateKey, string stage, IReadOnlyCollection<string> reasons, Guid? replayRunId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<int> LabelMatureCandidatesAsync(DateTime asOfUtc, Guid? replayRunId = null, int? maximumCandidates = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ResearchCandidateSnapshot>> GetCandidatesAsync(string? instrumentId = null, ResearchCandidateOutcome? outcome = null, int count = 100, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CandidateLabelSnapshot>> GetLabelsAsync(long researchCandidateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

static class CanonicalJson
{
    static readonly JsonSerializerOptions options = Create();
    static JsonSerializerOptions Create()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            // Trace correlation is intentionally random per invocation; signal identity and every decision field stay compared.
            if (info.Type == typeof(PipelineContext))
                foreach (var property in info.Properties.Where(x => x.Name == nameof(PipelineContext.CorrelationId)).ToArray()) info.Properties.Remove(property);
        });
        return new() { TypeInfoResolver = resolver };
    }
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, options);
}
