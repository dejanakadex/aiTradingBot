using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OptionsFactory = Microsoft.Extensions.Options.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

/// <summary>Offline bounded quote audit, with one continuous aggregate/feature state across input batches.</summary>
public static class ForexCollectionStreamAudit
{
    public static async Task<object> AnalyzeAsync(IMarketDatasetStore source, TradingSettings trading,
        ShortAggregateSettings settings, CanonicalFeatureSettings features, string instrumentId, DateTime from, DateTime to,
        ForexReplayCoverageEvidence? coverage = null, int batchSize = 4096, CancellationToken token = default)
    {
        if (from.Kind != DateTimeKind.Utc || to.Kind != DateTimeKind.Utc || from >= to || to - from > TimeSpan.FromDays(7)
            || settings.GetValidationErrors().Count != 0 || batchSize is < 1 or > 16384)
            throw new ArgumentException("Audit requires valid settings, bounded batch size and a positive UTC range of at most seven days.");
        var instrument = trading.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
        if (instrument.InstrumentType != InstrumentType.Forex) throw new ArgumentException("FX instrument required.");
        coverage = ForexReplayCoveragePolicy.FreezeEvidence(coverage, instrumentId, from, to);
        if (coverage?.Interruptions.Length > 10000) throw new ArgumentException("Too many coverage intervals.");
        var verification = await source.VerifyAsync(token);
        if (!verification.IsValid) throw new InvalidDataException("Frozen dataset integrity failed.");
        using var index = new ForexReplayIndex();
        await index.LoadAuditAsync(source, new(instrumentId, from, to) { ForexBatchSize = batchSize }, token);
        var latest = new LatestMarketDataService(OptionsFactory.Create(settings), OptionsFactory.Create(trading));
        var engine = new ForexFeatureService(latest, new FeatureEngine(features), OptionsFactory.Create(trading), OptionsFactory.Create(settings));
        var captured = settings.IntervalsSeconds.ToDictionary(x => x, _ => new Dictionary<DateTime, (int Count, bool Ready)>());
        var observed = settings.IntervalsSeconds.ToDictionary(x => x, _ => new Dictionary<DateTime, int>());
        using var featureHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var excludedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var diagnosticExamples = new List<object>();
        long lastSecond = -1;
        void Capture(DateTime now)
        {
            // Completion deadlines have whole-second resolution; no warmup reset at batch/page boundaries.
            var second = now.Ticks / TimeSpan.TicksPerSecond;
            if (second == lastSecond) return;
            lastSecond = second; latest.AdvanceTo(now);
            foreach (var interval in settings.IntervalsSeconds.Order())
                foreach (var bar in latest.GetCompletedForexAggregates(instrumentId, interval, now, settings.MaxCompletedPerInterval))
                {
                    if (bar.WindowStartUtc < from || bar.AvailableAtUtc > to || captured[interval].ContainsKey(bar.WindowStartUtc)) continue;
                    var frame = engine.Compute(instrumentId, interval, bar.AvailableAtUtc)!;
                    captured[interval][bar.WindowStartUtc] = (frame.SampleCount, frame.PriceFeaturesReady == true);
                    featureHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame) + "\n"));
                }
        }
        DateTime Floor(DateTime at, int seconds) => new(at.Ticks - at.Ticks % TimeSpan.FromSeconds(seconds).Ticks, DateTimeKind.Utc);
        long pairs = 0, diagnosticPairs = 0, diagnosticEvents = 0, suppressedByCoverage = 0, processedEvents = 0, sessionResets = 0;
        string? previousSession = null;
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var usableMinutes = new HashSet<DateTime>();
        DateTime? firstUsableReceived = null, lastUsableReceived = null;
        var interrupts = coverage?.Interruptions.ToArray() ?? [];
        var nextInterruption = 0; DateTime? blockedUntil = null;
        foreach (var (rows, rejected) in index.ReadAuditPairs(instrumentId, instrument.Symbol, settings, token))
        {
            var at = rows[0].ReceivedTimeUtc < from ? from : rows[0].ReceivedTimeUtc;
            if (at >= to) continue;
            processedEvents += rows.Length;
            while (nextInterruption < interrupts.Length && interrupts[nextInterruption].StartUtc <= at)
            {
                var gap = interrupts[nextInterruption++];
                if (gap.EndUtc < from) continue;
                Capture(gap.StartUtc < from ? from : gap.StartUtc);
                latest.ResetForexAggregates(instrumentId);
                blockedUntil = blockedUntil.HasValue && blockedUntil > gap.EndUtc ? blockedUntil : gap.EndUtc;
            }
            var key = rows[0].EventId[..^4]; var session = key[..key.LastIndexOf(':')];
            sessions.Add(session);
            if (previousSession != null && previousSession != session) { latest.ResetForexAggregates(instrumentId); sessionResets++; }
            previousSession = session;
            Capture(at);
            foreach (var row in rows)
                foreach (var interval in settings.IntervalsSeconds)
                {
                    var start = Floor(row.EventTimeUtc, interval);
                    var flags = (ForexQuoteTimePolicy.IsTooFarAhead(row.EventTimeUtc, row.ReceivedTimeUtc, settings) ? 1 : 0)
                        | (row.ReceivedTimeUtc - row.EventTimeUtc > TimeSpan.FromSeconds(settings.ForexMaximumQuoteAgeSeconds) ? 2 : 0)
                        | (rejected ? 4 : 0);
                    observed[interval][start] = observed[interval].GetValueOrDefault(start) | flags;
                }
            if (rejected)
            {
                diagnosticPairs++; diagnosticEvents += rows.Length;
                foreach (var row in rows)
                {
                    excludedHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"));
                    if (diagnosticExamples.Count < 1000) diagnosticExamples.Add(new { row.EventId, row.EventTimeUtc, row.ReceivedTimeUtc, row.QualityStatus, row.CanPersist, row.QualityReason });
                }
                latest.ResetForexAggregates(instrumentId);
                continue;
            }
            pairs++;
            if (blockedUntil.HasValue && at <= blockedUntil)
            {
                suppressedByCoverage++;
                foreach (var row in rows) excludedHash.AppendData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row) + "\n"));
                continue;
            }
            if (MarketDataSession.IsForexOpen(rows[0].EventTimeUtc)
                && rows[0].ReceivedTimeUtc - rows[0].EventTimeUtc <= TimeSpan.FromSeconds(settings.ForexMaximumQuoteAgeSeconds))
            {
                usableMinutes.Add(Floor(at, 60));
                firstUsableReceived ??= rows[0].ReceivedTimeUtc; lastUsableReceived = rows[0].ReceivedTimeUtc;
            }
            foreach (var row in rows.OrderBy(x => x.EventId, StringComparer.Ordinal))
                latest.Apply(new CanonicalMarketDataEvent
                {
                    EventId = row.EventId, InstrumentId = row.InstrumentId, Symbol = row.Symbol,
                    Kind = row.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
                    EventTimeUtc = row.EventTimeUtc, ReceivedTimeUtc = row.ReceivedTimeUtc,
                    Source = row.Source, Price = row.Price, Size = row.Size, IsFinal = row.IsFinal
                });
        }
        // Process trailing evidence too: a disconnect without a later tick must not invent a final bar.
        while (nextInterruption < interrupts.Length && interrupts[nextInterruption].StartUtc < to)
        {
            var gap = interrupts[nextInterruption++];
            if (gap.EndUtc < from) continue;
            Capture(gap.StartUtc < from ? from : gap.StartUtc); latest.ResetForexAggregates(instrumentId);
        }
        Capture(to);
        var intervals = new List<ForexIntervalAudit>();
        foreach (var interval in settings.IntervalsSeconds.Order())
        {
            var expected = new HashSet<DateTime>(); var start = Floor(from, interval);
            if (start < from) start = start.AddSeconds(interval);
            for (var at = start; at.AddSeconds(interval + settings.AllowedLatenessSeconds) <= to; at = at.AddSeconds(interval))
                if (MarketDataSession.IsForexOpen(at) && MarketDataSession.IsForexOpen(at.AddSeconds(interval).AddTicks(-1))) expected.Add(at);
            var finals = captured[interval].Where(x => expected.Contains(x.Key)).ToArray();
            var missing = expected.Where(x => observed[interval].ContainsKey(x) && !captured[interval].ContainsKey(x)).ToArray();
            var longestEmpty = 0; var empty = 0; DateTime? prior = null;
            foreach (var at in expected.Order())
            {
                if (prior.HasValue && at - prior != TimeSpan.FromSeconds(interval)) empty = 0;
                empty = observed[interval].ContainsKey(at) ? 0 : empty + 1;
                longestEmpty = Math.Max(longestEmpty, empty); prior = at;
            }
            intervals.Add(new(interval, expected.Count, expected.Count(x => !observed[interval].ContainsKey(x)), missing.Length,
                missing.Count(x => (observed[interval][x] & 1) != 0), missing.Count(x => (observed[interval][x] & 2) != 0),
                finals.Length, finals.Select(x => x.Value.Count).DefaultIfEmpty().Max(), finals.Count(x => x.Value.Count >= 26),
                finals.Count(x => x.Value.Ready), longestEmpty, finals.GroupBy(x => x.Value.Count).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count())));
        }
        var after = await source.VerifyAsync(token);
        if (!after.IsValid || verification.ManifestSha256 != after.ManifestSha256) throw new InvalidDataException("Frozen dataset changed during audit.");
        return new
        {
            Version = "fx-collection-stream-audit-v1", CanSendOrders = false, N5Complete = false, CanUseAsExecutionDataset = false,
            FromUtc = from, ToUtc = to, verification.ManifestSha256, index.CanonicalSha256, index.EventCount, index.DuplicateEvents,
            ProcessedEvents = processedEvents, CompleteHealthyPairs = pairs, RejectedDiagnosticPairs = diagnosticPairs, RejectedDiagnosticEvents = diagnosticEvents,
            DiagnosticExamples = diagnosticExamples, DiagnosticExamplesTruncated = diagnosticEvents > diagnosticExamples.Count,
            CoverageSuppressedPairs = suppressedByCoverage, Coverage = coverage, CallbackSessions = sessions.Count, CallbackSessionResets = sessionResets,
            UsableQuoteMinutes = usableMinutes.Count, FirstUsableReceivedUtc = firstUsableReceived, LastUsableReceivedUtc = lastUsableReceived,
            ExcludedContentSha256 = Convert.ToHexString(excludedHash.GetHashAndReset()), FeatureSha256 = Convert.ToHexString(featureHash.GetHashAndReset()),
            Intervals = intervals, Diagnostics = latest.GetForexDiagnostics(instrumentId),
            Limitations = new[] { "Quality reconstruction only: no cleaned dataset, execution labels, strategy outcomes or approvals.",
                "Known rejected diagnostic pairs are retained in source, counted, hashed and reset aggregate state. Healthy orphan/conflicting/malformed pairs still fail.",
                "Coverage suppresses pairs through the inclusive interruption end; supplied intervals reset warmup, including trailing gaps.",
                "No input page resets; memory bounded by input batch and at most seven days of bar/window summaries, not constant total memory.",
                "Only session-open full windows available by cutoff are counted; no fabricated empty bars. Missing quotes do not prove broker loss.",
                "Source may contain diagnostic rows and is not thereby approved for strict trade replay. The final holdout remains separate." }
        };
    }
}
