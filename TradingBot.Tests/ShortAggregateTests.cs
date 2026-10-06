using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ShortAggregateTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ClosesOnlyAfterLatenessBoundaryRejectsLateTradesAndDoesNotInventEmptyBars()
    {
        var latest = new LatestMarketDataService(Options.Create(new ShortAggregateSettings
        {
            IntervalsSeconds = [1, 5, 15], AllowedLatenessSeconds = 2
        }));
        latest.Apply(Trade("a", 0.5, 0.6, 100m, 2m));
        latest.Apply(Trade("b", 4.9, 5.2, 101m, 3m));
        Assert.DoesNotContain(latest.Get("SPY-ID")!.ShortAggregates, x => x.IntervalSeconds is 5 or 15);

        latest.AdvanceTo(Start.AddSeconds(7));
        Assert.Empty(latest.GetCompletedShortAggregates("SPY-ID", 5, Start.AddSeconds(6.999)));
        var first = Assert.Single(latest.GetCompletedShortAggregates("SPY-ID", 5, Start.AddSeconds(7)));
        Assert.Equal(Start, first.WindowStartUtc);
        Assert.Equal(Start.AddSeconds(5), first.WindowEndUtc);
        Assert.Equal(Start.AddSeconds(7), first.AvailableAtUtc);
        Assert.Equal(100m, first.Open);
        Assert.Equal(101m, first.Close);
        Assert.Equal(5m, first.Volume);
        Assert.Equal(2, first.TradeCount);

        latest.Apply(Trade("late", 4.95, 7.1, 999m, 99m));
        Assert.Equal(1, latest.Get("SPY-ID")!.LateShortAggregateTrades);
        Assert.Equal(first, Assert.Single(latest.GetCompletedShortAggregates("SPY-ID", 5, Start.AddSeconds(7.1))));

        latest.Apply(Trade("c", 20.1, 20.2, 102m, 1m));
        latest.AdvanceTo(Start.AddSeconds(27));
        var completed = latest.GetCompletedShortAggregates("SPY-ID", 5, Start.AddSeconds(27));
        Assert.Equal(2, completed.Count);
        Assert.Equal(Start.AddSeconds(20), completed[1].WindowStartUtc);
        Assert.Equal(3, completed[1].EmptyIntervalsBefore);
        Assert.DoesNotContain(completed, x => x.WindowStartUtc == Start.AddSeconds(5));
        Assert.Contains(latest.Get("SPY-ID")!.ShortAggregates, x => x.IntervalSeconds == 1);
    }

    [Fact]
    public void LiveAndReplayReceiveOrderProduceIdenticalAsOfAggregates()
    {
        var live = new LatestMarketDataService();
        var replay = new LatestMarketDataService();
        var events = new[]
        {
            Trade("one", 1, 1.1, 100m, 1m),
            Trade("two", 2, 2.1, 102m, 2m),
            Trade("three", 5, 5.1, 101m, 3m),
            Trade("four", 16, 16.1, 103m, 4m)
        };
        foreach (var marketEvent in events) live.Apply(marketEvent);
        live.AdvanceTo(Start.AddSeconds(22));

        foreach (var marketEvent in events.OrderBy(x => x.ReceivedTimeUtc)) replay.Apply(marketEvent);
        replay.AdvanceTo(Start.AddSeconds(22));

        foreach (var interval in new[] { 5, 15 })
        {
            Assert.Equal(
                live.GetCompletedShortAggregates("SPY-ID", interval, Start.AddSeconds(22)),
                replay.GetCompletedShortAggregates("SPY-ID", interval, Start.AddSeconds(22)));
            Assert.Empty(replay.GetCompletedShortAggregates("SPY-ID", interval, Start.AddSeconds(6)));
        }
        Assert.Equal(live.Get("SPY-ID")!.ShortAggregates, replay.Get("SPY-ID")!.ShortAggregates);
    }

    [Fact]
    public async Task PersistedTicksReproduceCompletedAggregatesWithoutFutureData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tradingbot-short-aggregates-{Guid.NewGuid():N}");
        try
        {
            var ticks = new[]
            {
                Trade("one", 1, 1.1, 100m, 1m),
                Trade("two", 2, 2.1, 102m, 2m),
                Trade("three", 5, 5.1, 101m, 3m),
                Trade("four", 16, 16.1, 103m, 4m)
            };
            var live = new LatestMarketDataService();
            foreach (var tick in ticks) live.Apply(tick);
            live.AdvanceTo(Start.AddSeconds(22));

            var store = new ParquetMarketDatasetStore(
                Options.Create(new DatasetStorageSettings { RootPath = root, QueueCapacity = 100, BatchSize = 10, FlushIntervalSeconds = 1 }),
                new FixedClock(Start),
                NullLogger<ParquetMarketDatasetStore>.Instance);
            var assessment = new MarketDataQualityAssessment(MarketDataQualityStatus.Healthy, "Valid trade.", true, false);
            await store.WriteBatchAsync(ticks.Select(tick => MarketDatasetRecord.From(tick, assessment, "market-data-v2")).ToArray());
            var stored = await store.ReadAsync(new DatasetQuery("SPY-ID", Start, Start.AddMinutes(1), ["trade"]));
            Assert.Equal(ticks.Length, stored.Count);

            var replay = new LatestMarketDataService();
            foreach (var row in stored.OrderBy(row => row.ReceivedTimeUtc).ThenBy(row => row.EventId, StringComparer.Ordinal))
            {
                replay.Apply(new CanonicalMarketDataEvent
                {
                    EventId = row.EventId, InstrumentId = row.InstrumentId, Symbol = row.Symbol,
                    Kind = MarketDataEventKind.Trade, EventTimeUtc = row.EventTimeUtc,
                    ReceivedTimeUtc = row.ReceivedTimeUtc, Source = row.Source,
                    Price = row.Price, Size = row.Size
                });
            }
            replay.AdvanceTo(Start.AddSeconds(22));

            foreach (var interval in new[] { 5, 15 })
            {
                foreach (var asOf in new[] { Start.AddSeconds(6), Start.AddSeconds(7), Start.AddSeconds(17), Start.AddSeconds(22) })
                {
                    Assert.Equal(
                        live.GetCompletedShortAggregates("SPY-ID", interval, asOf),
                        replay.GetCompletedShortAggregates("SPY-ID", interval, asOf));
                }
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DuplicateTradesAndIndependentInstrumentsDoNotContaminateAggregates()
    {
        var latest = new LatestMarketDataService();
        var spy = Trade("one", 1, 1.1, 100m, 2m);
        latest.Apply(spy);
        latest.Apply(spy);
        latest.Apply(spy with { EventId = "two", EventTimeUtc = Start.AddSeconds(0.5), ReceivedTimeUtc = Start.AddSeconds(1.2), Price = 99m, Size = 1m });
        latest.Apply(spy with { EventId = "qqq", InstrumentId = "QQQ-ID", Symbol = "QQQ", Price = 200m });
        latest.AdvanceTo(Start.AddSeconds(7));

        var spyBar = Assert.Single(latest.GetCompletedShortAggregates("SPY-ID", 5, Start.AddSeconds(7)));
        var qqqBar = Assert.Single(latest.GetCompletedShortAggregates("QQQ-ID", 5, Start.AddSeconds(7)));
        Assert.Equal(2, spyBar.TradeCount);
        Assert.Equal(3m, spyBar.Volume);
        Assert.Equal(99m, spyBar.Open);
        Assert.Equal(100m, spyBar.Close);
        Assert.Equal(1, qqqBar.TradeCount);
        Assert.Equal(200m, qqqBar.Close);
    }

    private static CanonicalMarketDataEvent Trade(string id, double eventOffset, double receiveOffset, decimal price, decimal size) => new()
    {
        EventId = id,
        InstrumentId = "SPY-ID",
        Symbol = "SPY",
        Kind = MarketDataEventKind.Trade,
        EventTimeUtc = Start.AddSeconds(eventOffset),
        ReceivedTimeUtc = Start.AddSeconds(receiveOffset),
        Source = "IBKR.TickByTick",
        Price = price,
        Size = size
    };

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
