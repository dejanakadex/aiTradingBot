using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

/// <summary>One instrument. Called under LatestMarketDataService's instrument lock, in receive order.</summary>
internal sealed class ForexQuoteAggregator
{
    public const string QuoteSource = "IBKR.TickByTick.Forex.BidAsk.v2";
    public const string CandleSource = "FX.QuoteMidpoint";
    private readonly ShortAggregateSettings _settings;
    private readonly Dictionary<string, CanonicalMarketDataEvent> _pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _recent = new();
    private readonly HashSet<DateTime> _invalidTimes = new();
    private readonly Dictionary<int, SortedDictionary<DateTime, Window>> _open = new();
    private readonly Dictionary<int, Queue<ForexQuoteBar>> _completed = new();
    private DateTime _now;
    private string? _session;
    private long _rejected, _late, _incomplete;

    public ForexQuoteAggregator(ShortAggregateSettings settings) { _settings = settings; Version = VersionFor(settings); }
    public string Version { get; }
    public ForexAggregateDiagnostics Diagnostics => new(_rejected, _late, _incomplete);
    public static string VersionFor(ShortAggregateSettings settings)
    {
        var contract = FormattableString.Invariant($"{string.Join(',', settings.IntervalsSeconds.Order())}|{settings.AllowedLatenessSeconds}|{settings.ForexMaximumQuoteAgeSeconds}|{settings.MaxCompletedPerInterval}");
        return "fx-midpoint-bars-v1+" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract))).ToLowerInvariant();
    }

    public void Apply(CanonicalMarketDataEvent item)
    {
        Advance(item.ReceivedTimeUtc);
        // v2 IDs identify the two halves of ONE broker callback. Legacy unpaired ticks are never guessed into a pair.
        var suffix = item.Kind == MarketDataEventKind.Bid ? ":bid" : ":ask";
        if (item.Kind is not (MarketDataEventKind.Bid or MarketDataEventKind.Ask)
            || item.Source != QuoteSource || !item.IsFinal || item.Price is not > 0m
            || !item.EventId.StartsWith("IBKR:FXQ:", StringComparison.Ordinal) || !item.EventId.EndsWith(suffix, StringComparison.Ordinal)
            || item.EventTimeUtc > item.ReceivedTimeUtc
            || item.ReceivedTimeUtc - item.EventTimeUtc > TimeSpan.FromSeconds(_settings.ForexMaximumQuoteAgeSeconds)
            || !MarketDataSession.IsForexOpen(item.EventTimeUtc))
        { Reject(item.EventTimeUtc); return; }
        var key = item.EventId[..^suffix.Length];
        if (_seen.Contains(key)) return;
        if (!_pending.Remove(key, out var other))
        {
            if (_pending.Count >= 128)
            {
                var oldest = _pending.Keys.First(); _invalidTimes.Add(_pending[oldest].EventTimeUtc);
                _pending.Remove(oldest); Remember(oldest); _incomplete++;
            }
            _pending[key] = item;
            return;
        }
        if (other.Kind == item.Kind) { _pending[key] = other; return; }
        Remember(key);
        if (other.EventTimeUtc != item.EventTimeUtc || other.ReceivedTimeUtc != item.ReceivedTimeUtc
            || other.Symbol != item.Symbol || other.InstrumentId != item.InstrumentId)
        { Reject(item.EventTimeUtc); Reject(other.EventTimeUtc); return; }
        var bid = item.Kind == MarketDataEventKind.Bid ? item.Price!.Value : other.Price!.Value;
        var ask = item.Kind == MarketDataEventKind.Ask ? item.Price!.Value : other.Price!.Value;
        if (ask < bid) { Reject(item.EventTimeUtc); return; }
        var session = key[..key.LastIndexOf(':')];
        if (_session != null && _session != session) Reset();
        _session = session;
        var price = bid / 2m + ask / 2m;
        var order = item.ReceivedTimeUtc.ToString("O", CultureInfo.InvariantCulture) + "|" + key;
        var late = false;
        foreach (var interval in _settings.IntervalsSeconds)
        {
            var width = TimeSpan.FromSeconds(interval).Ticks;
            var start = new DateTime(item.EventTimeUtc.Ticks - item.EventTimeUtc.Ticks % width, DateTimeKind.Utc);
            if (start.AddSeconds(interval + _settings.AllowedLatenessSeconds) <= _now) { late = true; continue; }
            if (!_open.TryGetValue(interval, out var windows)) _open[interval] = windows = new();
            if (!windows.TryGetValue(start, out var window))
                windows[start] = window = new Window(item.EventTimeUtc, order, price, bid, ask);
            else window.Add(item.EventTimeUtc, order, price, bid, ask);
        }
        if (late) _late++;
    }

    public void Advance(DateTime now)
    {
        if (now <= _now) return;
        _now = now;
        foreach (var key in _pending.Where(x => now - x.Value.ReceivedTimeUtc > TimeSpan.FromSeconds(_settings.ForexMaximumQuoteAgeSeconds + _settings.AllowedLatenessSeconds)).Select(x => x.Key).ToArray())
        { _invalidTimes.Add(_pending[key].EventTimeUtc); _pending.Remove(key); _incomplete++; Remember(key); }
        foreach (var (interval, windows) in _open)
        {
            foreach (var start in windows.Keys.TakeWhile(x => x.AddSeconds(interval + _settings.AllowedLatenessSeconds) <= now).ToArray())
            {
                var w = windows[start]; windows.Remove(start);
                var end = start.AddSeconds(interval);
                if (_invalidTimes.Any(x => x >= start && x < end)
                    || _pending.Values.Any(x => x.EventTimeUtc >= start && x.EventTimeUtc < end)) continue;
                if (!_completed.TryGetValue(interval, out var completed)) _completed[interval] = completed = new();
                var previousEnd = completed.Count == 0 ? start : completed.Last().WindowEndUtc;
                completed.Enqueue(new ForexQuoteBar
                {
                    Version = Version, IntervalSeconds = interval, WindowStartUtc = start, WindowEndUtc = start.AddSeconds(interval),
                    AvailableAtUtc = start.AddSeconds(interval + _settings.AllowedLatenessSeconds), LastQuoteTimeUtc = w.Last,
                    Open = w.Open, High = w.High, Low = w.Low, Close = w.Close,
                    LastBid = w.Bid, LastAsk = w.Ask, QuoteCount = w.Count, MeanSpread = w.SumSpread / w.Count,
                    MaximumSpread = w.MaxSpread, EmptyIntervalsBefore = (int)Math.Min(int.MaxValue, Math.Max(0, (start - previousEnd).TotalSeconds / interval))
                });
                while (completed.Count > _settings.MaxCompletedPerInterval) completed.Dequeue();
            }
        }
        _invalidTimes.RemoveWhere(x => x < now.AddSeconds(-_settings.IntervalsSeconds.Max() - _settings.AllowedLatenessSeconds));
    }

    public IReadOnlyList<ForexQuoteBar> Get(int interval, DateTime asOf, int count) =>
        _completed.TryGetValue(interval, out var bars) ? bars.Where(x => x.AvailableAtUtc <= asOf).TakeLast(count).ToArray() : [];

    public void Reset()
    {
        _incomplete += _pending.Count;
        _pending.Clear(); _open.Clear(); _completed.Clear(); _invalidTimes.Clear();
    }

    private void Reject(DateTime eventTime)
    {
        _rejected++;
        if (eventTime <= _now.AddSeconds(_settings.IntervalsSeconds.Max())) _invalidTimes.Add(eventTime);
    }

    private void Remember(string key)
    {
        if (!_seen.Add(key)) return;
        _recent.Enqueue(key);
        while (_recent.Count > 8192) _seen.Remove(_recent.Dequeue());
    }

    private sealed class Window(DateTime time, string order, decimal price, decimal bid, decimal ask)
    {
        public DateTime First = time, Last = time;
        private string _firstOrder = order, _lastOrder = order;
        public decimal Open = price, High = price, Low = price, Close = price;
        public decimal Bid = bid, Ask = ask, SumSpread = ask - bid, MaxSpread = ask - bid;
        public int Count = 1;
        public void Add(DateTime at, string ordering, decimal value, decimal b, decimal a)
        {
            if (at < First || at == First && StringComparer.Ordinal.Compare(ordering, _firstOrder) < 0)
            { First = at; _firstOrder = ordering; Open = value; }
            if (at > Last || at == Last && StringComparer.Ordinal.Compare(ordering, _lastOrder) >= 0)
            { Last = at; _lastOrder = ordering; Close = value; Bid = b; Ask = a; }
            High = Math.Max(High, value); Low = Math.Min(Low, value);
            SumSpread += a - b; MaxSpread = Math.Max(MaxSpread, a - b); Count++;
        }
    }
}
