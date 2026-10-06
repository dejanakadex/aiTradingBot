using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class LatestMarketDataService : ILatestMarketDataService
    {
        private readonly ShortAggregateSettings _settings;
        private readonly ConcurrentDictionary<string, InstrumentState> _states = new(StringComparer.OrdinalIgnoreCase);

        public LatestMarketDataService(IOptions<ShortAggregateSettings>? settings = null)
        {
            _settings = settings?.Value ?? new ShortAggregateSettings();
            var errors = _settings.GetValidationErrors();
            if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }

        public void Apply(CanonicalMarketDataEvent marketEvent)
        {
            if (marketEvent == null) throw new ArgumentNullException(nameof(marketEvent));
            var state = _states.GetOrAdd(marketEvent.InstrumentId, _ => new InstrumentState(marketEvent.InstrumentId, marketEvent.Symbol));
            lock (state.Sync)
            {
                AdvanceState(state, ToUtc(marketEvent.ReceivedTimeUtc));
                state.Symbol = marketEvent.Symbol;
                switch (marketEvent.Kind)
                {
                    case MarketDataEventKind.Bid:
                        state.Bid = marketEvent.Price;
                        state.BidTimeUtc = marketEvent.EventTimeUtc;
                        break;
                    case MarketDataEventKind.Ask:
                        state.Ask = marketEvent.Price;
                        state.AskTimeUtc = marketEvent.EventTimeUtc;
                        break;
                    case MarketDataEventKind.Trade:
                        if (!state.TryRegisterTrade(marketEvent.EventId)) break;
                        state.LastTrade = marketEvent.Price;
                        state.LastTradeTimeUtc = marketEvent.EventTimeUtc;
                        var lateForAnyInterval = false;
                        foreach (var interval in _settings.IntervalsSeconds)
                        {
                            lateForAnyInterval |= !UpdateAggregate(state, interval, marketEvent);
                        }
                        if (lateForAnyInterval) state.LateShortAggregateTrades++;
                        break;
                    case MarketDataEventKind.Bar:
                        if (!state.LastTradeTimeUtc.HasValue || marketEvent.EventTimeUtc >= state.LastTradeTimeUtc.Value)
                        {
                            state.LastTrade = marketEvent.Close;
                            state.LastTradeTimeUtc = marketEvent.EventTimeUtc;
                        }
                        break;
                }
            }
        }

        public void AdvanceTo(DateTime receivedTimeUtc)
        {
            var now = ToUtc(receivedTimeUtc);
            foreach (var state in _states.Values)
            {
                lock (state.Sync) AdvanceState(state, now);
            }
        }

        public IReadOnlyList<ShortIntervalBarSnapshot> GetCompletedShortAggregates(
            string instrumentId, int intervalSeconds, DateTime asOfUtc, int count = 100)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (!_settings.IntervalsSeconds.Contains(intervalSeconds)) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            if (!_states.TryGetValue(instrumentId, out var state)) return [];
            lock (state.Sync)
            {
                return state.Completed.TryGetValue(intervalSeconds, out var completed)
                    ? completed.Where(x => x.AvailableAtUtc <= ToUtc(asOfUtc)).TakeLast(count).ToArray()
                    : [];
            }
        }

        public LatestMarketDataSnapshot? Get(string instrumentIdOrSymbol)
        {
            if (string.IsNullOrWhiteSpace(instrumentIdOrSymbol)) return null;
            if (_states.TryGetValue(instrumentIdOrSymbol.Trim(), out var byId)) return Snapshot(byId);
            var bySymbol = _states.Values.FirstOrDefault(item => item.Symbol.Equals(instrumentIdOrSymbol.Trim(), StringComparison.OrdinalIgnoreCase));
            return bySymbol == null ? null : Snapshot(bySymbol);
        }

        public IReadOnlyList<LatestMarketDataSnapshot> GetAll() => _states.Values
            .OrderBy(item => item.Symbol)
            .Select(Snapshot)
            .ToArray();

        private bool UpdateAggregate(InstrumentState state, int intervalSeconds, CanonicalMarketDataEvent marketEvent)
        {
            var price = marketEvent.Price!.Value;
            var size = marketEvent.Size ?? 0m;
            var eventTime = ToUtc(marketEvent.EventTimeUtc);
            var windowTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
            var windowStart = new DateTime(eventTime.Ticks - eventTime.Ticks % windowTicks, DateTimeKind.Utc);

            var windowEnd = windowStart.AddSeconds(intervalSeconds);
            if (state.ClosedThroughUtc.TryGetValue(intervalSeconds, out var closedThrough) && windowEnd <= closedThrough)
            {
                return false;
            }

            if (!state.Open.TryGetValue(intervalSeconds, out var windows))
            {
                windows = new SortedDictionary<DateTime, MutableAggregate>();
                state.Open[intervalSeconds] = windows;
            }
            if (!windows.TryGetValue(windowStart, out var aggregate))
            {
                windows[windowStart] = new MutableAggregate(windowStart, eventTime, price, price, price, price, size, 1);
                return true;
            }

            if (eventTime < aggregate.FirstEventTimeUtc)
            {
                aggregate.FirstEventTimeUtc = eventTime;
                aggregate.Open = price;
            }
            if (eventTime >= aggregate.LastEventTimeUtc)
            {
                aggregate.LastEventTimeUtc = eventTime;
                aggregate.Close = price;
            }
            aggregate.High = Math.Max(aggregate.High, price);
            aggregate.Low = Math.Min(aggregate.Low, price);
            aggregate.Volume += size;
            aggregate.TradeCount++;
            return true;
        }

        private void AdvanceState(InstrumentState state, DateTime nowUtc)
        {
            if (nowUtc <= state.LastAdvanceUtc) return;
            state.LastAdvanceUtc = nowUtc;
            foreach (var interval in _settings.IntervalsSeconds)
            {
                var cutoff = nowUtc.AddSeconds(-_settings.AllowedLatenessSeconds);
                state.ClosedThroughUtc[interval] = cutoff;
                if (!state.Open.TryGetValue(interval, out var windows)) continue;
                foreach (var start in windows.Keys.TakeWhile(start => start.AddSeconds(interval) <= cutoff).ToArray())
                {
                    var current = windows[start];
                    windows.Remove(start);
                    if (!state.Completed.TryGetValue(interval, out var completed))
                    {
                        completed = new Queue<ShortIntervalBarSnapshot>();
                        state.Completed[interval] = completed;
                    }
                    var previousEnd = completed.Count == 0 ? start : completed.Last().WindowEndUtc;
                    completed.Enqueue(new ShortIntervalBarSnapshot
                    {
                        IntervalSeconds = interval,
                        WindowStartUtc = start,
                        WindowEndUtc = start.AddSeconds(interval),
                        AvailableAtUtc = start.AddSeconds(interval + _settings.AllowedLatenessSeconds),
                        LastEventTimeUtc = current.LastEventTimeUtc,
                        Open = current.Open,
                        High = current.High,
                        Low = current.Low,
                        Close = current.Close,
                        Volume = current.Volume,
                        TradeCount = current.TradeCount,
                        EmptyIntervalsBefore = Math.Max(0, (int)((start - previousEnd).TotalSeconds / interval))
                    });
                    while (completed.Count > _settings.MaxCompletedPerInterval) completed.Dequeue();
                }
            }
        }

        private static LatestMarketDataSnapshot Snapshot(InstrumentState state)
        {
            lock (state.Sync)
            {
                var asOf = new[] { state.BidTimeUtc, state.AskTimeUtc, state.LastTradeTimeUtc }
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .DefaultIfEmpty()
                    .Max();
                return new LatestMarketDataSnapshot
                {
                    InstrumentId = state.InstrumentId,
                    Symbol = state.Symbol,
                    Bid = state.Bid,
                    Ask = state.Ask,
                    LastTrade = state.LastTrade,
                    BidTimeUtc = state.BidTimeUtc,
                    AskTimeUtc = state.AskTimeUtc,
                    LastTradeTimeUtc = state.LastTradeTimeUtc,
                    AsOfUtc = asOf == default ? null : asOf,
                    ShortAggregates = state.Completed.OrderBy(item => item.Key)
                        .Where(item => item.Value.Count > 0)
                        .Select(item => item.Value.Last())
                        .ToArray(),
                    LateShortAggregateTrades = state.LateShortAggregateTrades
                };
            }
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };

        private sealed class InstrumentState
        {
            public InstrumentState(string instrumentId, string symbol)
            {
                InstrumentId = instrumentId;
                Symbol = symbol;
            }

            public object Sync { get; } = new();
            public string InstrumentId { get; }
            public string Symbol { get; set; }
            public decimal? Bid { get; set; }
            public decimal? Ask { get; set; }
            public decimal? LastTrade { get; set; }
            public DateTime? BidTimeUtc { get; set; }
            public DateTime? AskTimeUtc { get; set; }
            public DateTime? LastTradeTimeUtc { get; set; }
            public Dictionary<int, SortedDictionary<DateTime, MutableAggregate>> Open { get; } = new();
            public Dictionary<int, Queue<ShortIntervalBarSnapshot>> Completed { get; } = new();
            public Dictionary<int, DateTime> ClosedThroughUtc { get; } = new();
            public DateTime LastAdvanceUtc { get; set; }
            public long LateShortAggregateTrades { get; set; }
            private readonly HashSet<string> _seenTradeIds = new(StringComparer.Ordinal);
            private readonly Queue<string> _recentTradeIds = new();

            public bool TryRegisterTrade(string eventId)
            {
                if (string.IsNullOrWhiteSpace(eventId)) return true;
                if (!_seenTradeIds.Add(eventId)) return false;
                _recentTradeIds.Enqueue(eventId);
                while (_recentTradeIds.Count > 8192) _seenTradeIds.Remove(_recentTradeIds.Dequeue());
                return true;
            }
        }

        private sealed class MutableAggregate
        {
            public MutableAggregate(DateTime windowStartUtc, DateTime lastEventTimeUtc, decimal open, decimal high, decimal low, decimal close, decimal volume, int tradeCount)
            {
                WindowStartUtc = windowStartUtc;
                FirstEventTimeUtc = lastEventTimeUtc;
                LastEventTimeUtc = lastEventTimeUtc;
                Open = open;
                High = high;
                Low = low;
                Close = close;
                Volume = volume;
                TradeCount = tradeCount;
            }

            public DateTime WindowStartUtc { get; }
            public DateTime FirstEventTimeUtc { get; set; }
            public DateTime LastEventTimeUtc { get; set; }
            public decimal Open { get; set; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
            public int TradeCount { get; set; }
        }
    }
}
