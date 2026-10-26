using OptionsFactory = Microsoft.Extensions.Options.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

/// <summary>Receive-clock reconstruction of saved quotes. No broker, database, approvals or orders.</summary>
public static class ForexCollectionAudit
{
    public static ForexCollectionAuditReport Analyze(IReadOnlyList<MarketDatasetRecord> input,
        TradingSettings trading, ShortAggregateSettings aggregates, CanonicalFeatureSettings features,
        string instrumentId, DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || fromUtc >= toUtc
            || toUtc - fromUtc > TimeSpan.FromDays(7))
            throw new ArgumentException("Collection audit requires an explicit UTC range of at most seven days.");
        if (input.Count > 200_000) throw new ArgumentException("Collection audit supports at most 200,000 quote events per batch.");
        var instrument = trading.GetConfiguredInstruments().SingleOrDefault(x => x.InstrumentId == instrumentId);
        if (instrument?.InstrumentType != InstrumentType.Forex) throw new ArgumentException("Configured FX instrument required.");
        var records = input.Where(x => x.EventTimeUtc >= fromUtc && x.EventTimeUtc < toUtc && x.ReceivedTimeUtc <= toUtc)
            .OrderBy(x => x.ReceivedTimeUtc).ThenBy(x => x.EventId, StringComparer.Ordinal).ToArray();
        // Fail explicitly on an incompatible dataset rather than infer callback pairs from unrelated quotes.
        if (records.Any(x => x.InstrumentId != instrumentId || x.Symbol != instrument.Symbol
            || x.Source != ForexQuoteAggregator.QuoteSource || x.DataType is not ("bid" or "ask")
            || !x.EventId.StartsWith("IBKR:FXQ:", StringComparison.Ordinal)
            || !x.EventId.EndsWith(":" + x.DataType, StringComparison.Ordinal)
            || x.EventTimeUtc.Kind != DateTimeKind.Utc || x.ReceivedTimeUtc.Kind != DateTimeKind.Utc
            || !x.IsFinal || !x.CanPersist || x.QualityStatus != "Healthy"))
            throw new InvalidDataException("Collection audit requires healthy, final, v2 paired FX provenance and UTC timestamps.");
        if (records.Select(x => x.EventId).Distinct(StringComparer.Ordinal).Count() != records.Length)
            throw new InvalidDataException("Duplicate quote identities must be resolved before auditing.");
        static string PairKey(MarketDatasetRecord x) => x.EventId[..^(x.DataType.Length + 1)];
        var pairs = records.GroupBy(PairKey).ToArray();
        var complete = pairs.Count(g => g.Count() == 2 && g.Select(x => x.DataType).Distinct().Count() == 2
            && g.Select(x => x.EventTimeUtc).Distinct().Count() == 1 && g.Select(x => x.ReceivedTimeUtc).Distinct().Count() == 1);
        var latest = new LatestMarketDataService(OptionsFactory.Create(aggregates), OptionsFactory.Create(trading));
        var featureService = new ForexFeatureService(latest, new FeatureEngine(features), OptionsFactory.Create(trading), OptionsFactory.Create(aggregates));
        var captured = aggregates.IntervalsSeconds.ToDictionary(x => x, _ => new Dictionary<DateTime, (int Count, bool Ready)>());
        void Capture(DateTime now)
        {
            latest.AdvanceTo(now);
            foreach (var interval in aggregates.IntervalsSeconds)
            {
                foreach (var bar in latest.GetCompletedForexAggregates(instrumentId, interval, now, aggregates.MaxCompletedPerInterval))
                {
                    if (bar.WindowStartUtc < fromUtc || bar.AvailableAtUtc > toUtc || captured[interval].ContainsKey(bar.WindowStartUtc)) continue;
                    var frame = featureService.Compute(instrumentId, interval, bar.AvailableAtUtc)!;
                    captured[interval][bar.WindowStartUtc] = (frame.SampleCount, frame.PriceFeaturesReady == true);
                }
            }
        }
        foreach (var record in records)
        {
            Capture(record.ReceivedTimeUtc);
            latest.Apply(new CanonicalMarketDataEvent
            {
                EventId = record.EventId, InstrumentId = record.InstrumentId, Symbol = record.Symbol,
                Kind = record.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
                EventTimeUtc = record.EventTimeUtc, ReceivedTimeUtc = record.ReceivedTimeUtc,
                Source = record.Source, Price = record.Price, Size = record.Size, IsFinal = record.IsFinal
            });
        }
        Capture(toUtc); // Never advance past the requested as-of cutoff to manufacture a final bar.
        var intervals = new List<ForexIntervalAudit>();
        foreach (var interval in aggregates.IntervalsSeconds.Order())
        {
            DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.FromSeconds(interval).Ticks, DateTimeKind.Utc);
            var expected = new HashSet<DateTime>();
            var start = Floor(fromUtc);
            if (start < fromUtc) start = start.AddSeconds(interval);
            for (var at = start; at.AddSeconds(interval + aggregates.AllowedLatenessSeconds) <= toUtc; at = at.AddSeconds(interval))
                if (MarketDataSession.IsForexOpen(at) && MarketDataSession.IsForexOpen(at.AddSeconds(interval).AddTicks(-1))) expected.Add(at);
            var observed = records.GroupBy(x => Floor(x.EventTimeUtc)).ToDictionary(x => x.Key, x => x.ToArray());
            var final = captured[interval].Where(x => expected.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            var missing = expected.Where(x => observed.ContainsKey(x) && !final.ContainsKey(x)).ToArray();
            var longestEmpty = 0; var emptyRun = 0; DateTime? previous = null;
            foreach (var at in expected.Order())
            {
                if (previous.HasValue && at - previous.Value != TimeSpan.FromSeconds(interval)) emptyRun = 0;
                emptyRun = observed.ContainsKey(at) ? 0 : emptyRun + 1;
                longestEmpty = Math.Max(longestEmpty, emptyRun); previous = at;
            }
            intervals.Add(new(interval, expected.Count, expected.Count(x => !observed.ContainsKey(x)), missing.Length,
                missing.Count(x => observed[x].Any(r => r.EventTimeUtc > r.ReceivedTimeUtc)),
                missing.Count(x => observed[x].Any(r => r.ReceivedTimeUtc - r.EventTimeUtc > TimeSpan.FromSeconds(aggregates.ForexMaximumQuoteAgeSeconds))),
                final.Count, final.Values.Select(x => x.Count).DefaultIfEmpty().Max(), final.Values.Count(x => x.Count >= 26),
                final.Values.Count(x => x.Ready), longestEmpty,
                final.Values.GroupBy(x => x.Count).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count())));
        }
        var ages = records.Select(x => (x.ReceivedTimeUtc - x.EventTimeUtc).TotalMilliseconds).Order().ToArray();
        double? Percentile(double p) => ages.Length == 0 ? null : ages[(int)Math.Floor((ages.Length - 1) * p)];
        return new(instrumentId, fromUtc, toUtc, ForexQuoteAggregator.VersionFor(aggregates), records.Length,
            complete, pairs.Length - complete, pairs.Select(x => x.Key[..x.Key.LastIndexOf(':')]).Distinct().Count(),
            records.Length == 0 ? null : records.Min(x => x.EventTimeUtc), records.Length == 0 ? null : records.Max(x => x.EventTimeUtc),
            Percentile(0), Percentile(.5), Percentile(.95), records.Count(x => x.EventTimeUtc > x.ReceivedTimeUtc),
            records.Count(x => x.ReceivedTimeUtc - x.EventTimeUtc > TimeSpan.FromSeconds(aggregates.ForexMaximumQuoteAgeSeconds)),
            latest.GetForexDiagnostics(instrumentId), intervals);
    }
}
