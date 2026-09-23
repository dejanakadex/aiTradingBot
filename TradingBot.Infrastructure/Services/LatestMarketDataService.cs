using System.Collections.Concurrent;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services
{
    public sealed class LatestMarketDataService : ILatestMarketDataService
    {
        private static readonly int[] AggregateIntervals = [5, 15];
        private readonly ConcurrentDictionary<string, InstrumentState> _states = new(StringComparer.OrdinalIgnoreCase);

        public void Apply(CanonicalMarketDataEvent marketEvent)
        {
            if (marketEvent == null) throw new ArgumentNullException(nameof(marketEvent));
            var state = _states.GetOrAdd(marketEvent.InstrumentId, _ => new InstrumentState(marketEvent.InstrumentId, marketEvent.Symbol));
            lock (state.Sync)
            {
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
                        state.LastTrade = marketEvent.Price;
                        state.LastTradeTimeUtc = marketEvent.EventTimeUtc;
                        foreach (var interval in AggregateIntervals)
                        {
                            UpdateAggregate(state, interval, marketEvent);
                        }
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

        private static void UpdateAggregate(InstrumentState state, int intervalSeconds, CanonicalMarketDataEvent marketEvent)
        {
            var price = marketEvent.Price!.Value;
            var size = marketEvent.Size ?? 0m;
            var eventTime = ToUtc(marketEvent.EventTimeUtc);
            var windowTicks = TimeSpan.FromSeconds(intervalSeconds).Ticks;
            var windowStart = new DateTime(eventTime.Ticks - eventTime.Ticks % windowTicks, DateTimeKind.Utc);

            if (!state.Aggregates.TryGetValue(intervalSeconds, out var aggregate) || aggregate.WindowStartUtc != windowStart)
            {
                state.Aggregates[intervalSeconds] = new MutableAggregate(windowStart, eventTime, price, price, price, price, size, 1);
                return;
            }

            aggregate.LastEventTimeUtc = eventTime;
            aggregate.High = Math.Max(aggregate.High, price);
            aggregate.Low = Math.Min(aggregate.Low, price);
            aggregate.Close = price;
            aggregate.Volume += size;
            aggregate.TradeCount++;
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
                    ShortAggregates = state.Aggregates
                        .OrderBy(item => item.Key)
                        .Select(item => new ShortIntervalBarSnapshot
                        {
                            IntervalSeconds = item.Key,
                            WindowStartUtc = item.Value.WindowStartUtc,
                            LastEventTimeUtc = item.Value.LastEventTimeUtc,
                            Open = item.Value.Open,
                            High = item.Value.High,
                            Low = item.Value.Low,
                            Close = item.Value.Close,
                            Volume = item.Value.Volume,
                            TradeCount = item.Value.TradeCount
                        })
                        .ToArray()
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
            public Dictionary<int, MutableAggregate> Aggregates { get; } = new();
        }

        private sealed class MutableAggregate
        {
            public MutableAggregate(DateTime windowStartUtc, DateTime lastEventTimeUtc, decimal open, decimal high, decimal low, decimal close, decimal volume, int tradeCount)
            {
                WindowStartUtc = windowStartUtc;
                LastEventTimeUtc = lastEventTimeUtc;
                Open = open;
                High = high;
                Low = low;
                Close = close;
                Volume = volume;
                TradeCount = tradeCount;
            }

            public DateTime WindowStartUtc { get; }
            public DateTime LastEventTimeUtc { get; set; }
            public decimal Open { get; }
            public decimal High { get; set; }
            public decimal Low { get; set; }
            public decimal Close { get; set; }
            public decimal Volume { get; set; }
            public int TradeCount { get; set; }
        }
    }
}
