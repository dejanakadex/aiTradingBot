using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class CompletedTradeAnalysisTests
{
    private static CompletedTradeAnalysisService Service(ForexResearchFixture f) => new(f, f, NullLogger<CompletedTradeAnalysisService>.Instance, Options.Create(f.Trading));
    private static async Task<int> Seed(ForexResearchFixture f, bool complete = true)
    {
        using var db = f.CreateDbContext();
        var trade = new Trade { Symbol = "EURUSD", OpenedUtc = f.UtcNow.AddSeconds(-30), ClosedUtc = f.UtcNow, EntryPrice = 1.1m, Size = 0 };
        db.Trades.Add(trade);
        var entry = new OrderRecord { BrokerOrderId = "entry", Side = "BUY", Symbol = "EURUSD", FilledQuantity = 10000 };
        var exit = new OrderRecord { BrokerOrderId = "exit", Side = "SELL", Symbol = "EURUSD", FilledQuantity = 10000 };
        db.OrderRecords.AddRange(entry, exit); await db.SaveChangesAsync();
        db.ExecutionRecords.Add(new() { BrokerExecutionId = "buy", OrderRecordId = entry.Id, Quantity = 10000, Price = 1.1m, TimestampUtc = trade.OpenedUtc, Commission = 2, RawJson = "{\"commissionCurrency\":\"USD\"}" });
        if (complete) db.ExecutionRecords.Add(new() { BrokerExecutionId = "sell", OrderRecordId = exit.Id, Quantity = 10000, Price = 1.102m, TimestampUtc = f.UtcNow, Commission = 2, RawJson = "{\"commissionCurrency\":\"USD\"}" });
        var lifecycle = new ExitManagementRecord { TradeId = trade.Id, Symbol = "EURUSD", InstrumentId = "EURUSD", StrategyId = "patterns",
            State = ExitManagementState.Closed, FilledQuantity = 10000, ExitedQuantity = 10000, EntryBrokerOrderId = "entry", ExitBrokerOrderId = "exit",
            OpenedUtc = trade.OpenedUtc, ClosedUtc = f.UtcNow, RawJson = "{}" };
        db.ExitManagementRecords.Add(lifecycle); await db.SaveChangesAsync(); return lifecycle.Id;
    }

    [Fact]
    public async Task MissingExecutionDefersUntilReconciledAndConcurrentWorkersKeepOneResult()
    {
        using var f = new ForexResearchFixture(); var id = await Seed(f, false);
        Assert.False(await Service(f).SynchronizeAsync(id));
        using (var db = f.CreateDbContext())
        {
            Assert.Empty(db.PostTradeAnalysisRecords);
            db.ResearchCandidateRecords.Add(new() { RecordKey = "unrelated", CandidateKey = "unrelated", InstrumentId = "EURUSD", SignalId = null });
            var order = await db.OrderRecords.SingleAsync(x => x.BrokerOrderId == "exit");
            db.ExecutionRecords.Add(new() { BrokerExecutionId = "sell", OrderRecordId = order.Id, Quantity = 10000, Price = 1.102m,
                TimestampUtc = f.UtcNow, Commission = 2, RawJson = "{\"commissionCurrency\":\"USD\"}" });
            await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(Task.Run(() => Service(f).SynchronizeAsync(id)), Task.Run(() => Service(f).SynchronizeAsync(id)));
        Assert.Single(results, x => x);
        using var check = f.CreateDbContext(); var record = Assert.Single(check.PostTradeAnalysisRecords);
        Assert.Equal(16m, record.NetProfitLoss); Assert.True(record.FeesComplete);
        Assert.DoesNotContain("unrelated", record.EntryContextJson);
        var displayed = Assert.Single(await new TradeDashboardService(f).GetTradesAsync());
        Assert.Equal(10000m, displayed.Quantity); Assert.Equal(16m, displayed.NetProfitLoss); Assert.Equal("Win", displayed.TradeResult);
    }

    [Fact]
    public async Task FailedProjectionRetriesFromDurableClosureWithoutPartialAnalysis()
    {
        using var f = new ForexResearchFixture(); var id = await Seed(f);
        f.SaveInterceptor = new FailProjectionOnce();
        await Assert.ThrowsAsync<IOException>(() => Service(f).SynchronizeAsync(id));
        using (var db = f.CreateDbContext()) Assert.Empty(db.PostTradeAnalysisRecords);
        Assert.True(await Service(f).SynchronizeAsync(id));
        Assert.False(await Service(f).SynchronizeAsync(id));
        using var check = f.CreateDbContext(); Assert.Single(check.PostTradeAnalysisRecords);
    }

    [Fact]
    public async Task AutomaticResultSupersedesLegacyStatisticsWithoutDeletingLegacyEvidence()
    {
        using var f = new ForexResearchFixture(); var id = await Seed(f); await Service(f).SynchronizeAsync(id);
        using (var db = f.CreateDbContext())
        {
            var result = await db.PostTradeAnalysisRecords.SingleAsync();
            db.PostTradeAnalysisRecords.Add(new() { SourceTradeId = result.SourceTradeId, Symbol = "EURUSD", Pattern = result.Pattern, NetProfitLoss = 999 });
            await db.SaveChangesAsync();
        }
        var stats = await new PostTradeAnalysisService(f).GetPatternPerformanceAsync("Unknown");
        Assert.Equal(1, stats.Trades); Assert.Equal(16m, stats.AverageNetReturn);
        using var check = f.CreateDbContext(); Assert.Equal(2, check.PostTradeAnalysisRecords.Count());
    }

    private sealed class FailProjectionOnce : SaveChangesInterceptor
    {
        bool fail = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (fail && data.Context!.ChangeTracker.Entries<PostTradeAnalysisRecord>().Any()) { fail = false; throw new IOException("Test projection write failure."); }
            return ValueTask.FromResult(result);
        }
    }
}
