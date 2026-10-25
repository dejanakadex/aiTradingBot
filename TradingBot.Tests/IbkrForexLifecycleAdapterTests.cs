using System.Collections.Concurrent;
using System.Reflection;
using IBApi;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.DTOs;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;

namespace TradingBot.Tests;

public sealed class IbkrForexLifecycleAdapterTests
{
    private static IbkrBrokerService Broker() => new(new IbkrOptions(), NullLogger<IbkrBrokerService>.Instance,
        tradingSettings: Options.Create(IbkrFxContractTests.Settings()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositionsOrdersAndFillsKeepPairIdentityForBothProtocols(bool protobuf)
    {
        using var broker = Broker();
        var positions = new List<PositionDto>();
        var statuses = new List<OrderStatusDto>();
        var fills = new List<OrderStatusDto>();
        broker.PositionUpdated += p => { positions.Add(p); return Task.CompletedTask; };
        broker.OrderStatusUpdated += s => { statuses.Add(s); return Task.CompletedTask; };
        broker.OrderFilled += s => { fills.Add(s); return Task.CompletedTask; };
        foreach (var (quote, id) in new[] { ("USD", 10), ("GBP", 11) })
        {
            if (protobuf)
            {
                var contract = new IBApi.protobuf.Contract { Symbol = "EUR", SecType = "CASH", Currency = quote, ConId = id };
                broker.positionProtoBuf(new() { Account = "DU123", Contract = contract, Position_ = "1234.56", AvgCost = 1.1 });
                broker.openOrderProtoBuf(new() { OrderId = id, Contract = contract,
                    Order = new() { Account = "DU123", Action = "BUY", OrderType = "LMT", TotalQuantity = "20000", LmtPrice = 1.1 } });
                broker.execDetailsProtoBuf(new() { Contract = contract, Execution = new() { OrderId = id, AcctNumber = "DU123",
                    ExecId = $"exec-{id}", Shares = "1234.56", CumQty = "1234.56", AvgPrice = 1.1, Price = 1.1 } });
            }
            else
            {
                var contract = new Contract { Symbol = "EUR", SecType = "CASH", Currency = quote, ConId = id };
                broker.position("DU123", contract, 1234.56m, 1.1);
                broker.openOrder(id, contract, new Order { Account = "DU123", Action = "BUY", OrderType = "LMT", TotalQuantity = 20000, LmtPrice = 1.1 }, new());
                broker.execDetails(0, contract, new Execution { OrderId = id, AcctNumber = "DU123", ExecId = $"exec-{id}",
                    Shares = 1234.56m, CumQty = 1234.56m, AvgPrice = 1.1, Price = 1.1 });
            }
            broker.orderStatus(id, "Submitted", 1234.56m, 18765.44m, 1.1, 0, 0, 1.1, 0, "", 0);
            Assert.Equal("EUR" + quote, positions[^1].Symbol);
            Assert.Equal(1234.56m, positions[^1].Quantity);
            Assert.Equal(quote, positions[^1].Currency);
            Assert.Equal(id, positions[^1].ContractId);
            Assert.Equal("EUR" + quote, statuses[^1].Symbol);
            Assert.Equal(20000, statuses[^1].RequestedQuantity);
            Assert.Equal("DU123", statuses[^1].AccountId);
            Assert.Equal("EUR" + quote, fills[^1].Symbol);
            Assert.Equal(1234.56m, fills[^1].IndividualFillQuantity);
        }
        Assert.Equal(2, positions.Select(x => x.Symbol).Distinct().Count());
        Assert.Equal("SPY", IbkrBrokerService.CanonicalBrokerSymbol("SPY", "STK", "USD"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommissionCurrencySurvivesEarlyAndLateProtobufCallbacks(bool early)
    {
        using var broker = Broker();
        var fills = new List<OrderStatusDto>();
        broker.OrderFilled += s => { fills.Add(s); return Task.CompletedTask; };
        void Fee() => broker.commissionAndFeesReportProtoBuf(new() { ExecId = "fx-fill", CommissionAndFees = 2, Currency = "USD" });
        if (early) Fee();
        broker.execDetailsProtoBuf(new() { Contract = new() { Symbol = "EUR", SecType = "CASH", Currency = "USD" },
            Execution = new() { OrderId = 12, AcctNumber = "DU123", ExecId = "fx-fill", Shares = "20000", CumQty = "20000", Price = 1.1, AvgPrice = 1.1 } });
        if (!early) Fee();
        Assert.Equal(2m, fills[^1].Commission);
        Assert.Equal("USD", fills[^1].CommissionCurrency);
        Assert.Equal("DU123", fills[^1].AccountId);
        Assert.Equal(!early, fills[^1].IsCommissionUpdate);
        Assert.Equal("fx-fill", fills[^1].BrokerExecutionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarketRuleCallbacksRetainDecimalBands(bool protobuf)
    {
        using var broker = Broker();
        var pending = (ConcurrentDictionary<int, TaskCompletionSource<ForexPriceBand[]>>)typeof(IbkrBrokerService)
            .GetField("_marketRules", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(broker)!;
        var completion = new TaskCompletionSource<ForexPriceBand[]>(); pending[42] = completion;
        if (protobuf)
        {
            var rule = new IBApi.protobuf.MarketRule { MarketRuleId = 42 };
            rule.PriceIncrements.Add(new IBApi.protobuf.PriceIncrement { LowEdge = 0, Increment = .00001 });
            broker.marketRuleProtoBuf(rule);
        }
        else broker.marketRule(42, [new() { LowEdge = 0, Increment = .00001 }]);
        Assert.Equal(new ForexPriceBand(0, .00001m), Assert.Single(await completion.Task.WaitAsync(TimeSpan.FromSeconds(1))));
    }

    [Theory]
    [InlineData(19999, 1.1, false)]
    [InlineData(20000, 1.100005, false)]
    [InlineData(20000.001, 1.1, false)]
    [InlineData(20000.01, 1.10001, true)]
    public void OrdersCannotInventSizePermissionOrRoundResidualUp(decimal units, decimal price, bool valid)
    {
        var rules = new ForexOrderRules(120, DateTime.UtcNow, "test", 20000, 100000, .01m, 42, [new(0, .00001m)]);
        var request = new OrderRequestDto { Quantity = units, LimitPrice = price };
        if (valid) IbkrBrokerService.ValidateForexOrder(request, rules);
        else Assert.Throws<InvalidOperationException>(() => IbkrBrokerService.ValidateForexOrder(request, rules));
    }

    [Fact]
    public async Task MigrationAddsProofWithoutChangingExistingReservations()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new TradingBotDbContext(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().Last(x => x != "20261025180000_AddForexExecutionProofs");
        await migrator.MigrateAsync(previous);
        var id = Guid.NewGuid();
        db.PortfolioRiskReservationRecords.Add(new() { Id = id, ReservationKey = "keep", Symbol = "SPY" });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        Assert.Equal("SPY", (await db.PortfolioRiskReservationRecords.SingleAsync()).Symbol);
        db.ForexExecutionProofRecords.Add(new() { ReservationId = id, InstrumentId = "EURUSD", CreatedUtc = DateTime.UtcNow, RequestJson = "{}", AssessmentJson = "{}" });
        await db.SaveChangesAsync();
        Assert.Single(await db.ForexExecutionProofRecords.ToArrayAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("EUR", null)]
    [InlineData("BASE", null)]
    [InlineData("", null)]
    public void AccountSummaryDoesNotInventUsdCurrency(string currency, string? expected)
    {
        var type = typeof(IbkrBrokerService).GetNestedType("AccountSummaryRequest", BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(type, "DU123")!;
        foreach (var tag in new[] { "NetLiquidation", "AvailableFunds", "BuyingPower", "MaintMarginReq" })
            type.GetMethod("Record")!.Invoke(request, ["DU123", tag, "100000", tag == "BuyingPower" ? currency : "USD"]);
        Assert.Equal(expected, ((AccountInfo)type.GetMethod("Build")!.Invoke(request, null)!).Currency);
    }

    [Fact]
    public void ExecutionProfilesAreOptInAndRejectDuplicateOrUnsupportedIntervals()
    {
        new ForexExecutionSettings().Validate();
        var profile = new ForexExecutionProfile { InstrumentId = "EURUSD", MinimumBaseUnits = 20000, MaximumBaseUnits = 100000,
            ReviewedAtUtc = DateTime.UtcNow, EvidenceSource = "test", TrailingIntervalSeconds = 5 };
        new ForexExecutionSettings { Profiles = [profile] }.Validate();
        Assert.Throws<ArgumentException>(() => new ForexExecutionSettings { Profiles = [profile, profile] }.Validate());
        profile.TrailingIntervalSeconds = 10;
        Assert.Throws<ArgumentException>(() => new ForexExecutionSettings { Profiles = [profile] }.Validate());
    }
}
