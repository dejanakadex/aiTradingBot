using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class ForexCollectionProfileTests
{
    private static IConfiguration Config()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "TradingBot.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return new ConfigurationBuilder().SetBasePath(Path.Combine(root.FullName, "TradingBot.Web"))
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.ForexCollection.json").Build();
    }

    [Fact]
    public void ActualMergedProfileEnablesOnlyEurUsdCollectionAndKeepsUnapprovedModelGates()
    {
        var config = Config();
        var trading = config.GetSection("TradingSettings").Get<TradingSettings>()!;
        Assert.Empty(trading.GetValidationErrors());
        Assert.False(trading.Enabled); Assert.False(trading.LiveTradingExplicitlyEnabled); Assert.False(trading.ForexPaperExecutionEnabled);
        Assert.Equal(TradingOperatingMode.AnalysisOnly, trading.OperatingMode);
        var fx = Assert.Single(trading.GetEnabledInstruments());
        Assert.Equal("FX-CASH-EUR-USD-IDEALPRO", fx.InstrumentId); Assert.Equal(InstrumentType.Forex, fx.InstrumentType);
        Assert.All(trading.GetConfiguredInstruments(), x => Assert.False(x.TradingEnabled));
        var stock = Assert.Single(trading.GetConfiguredInstruments(), x => x.Symbol == "SPY");
        Assert.Equal("STK|SPY|SMART|USD", stock.ContractKey); Assert.False(stock.Enabled);
        var patterns = config.GetSection("ForexPatterns").Get<ForexPatternSettings>()!;
        patterns.ReplaceConfiguredArrays(config.GetSection("ForexPatterns"));
        var aggregates = config.GetSection("ShortAggregates").Get<ShortAggregateSettings>()!;
        aggregates.IntervalsSeconds = config.GetSection("ShortAggregates:IntervalsSeconds").Get<int[]>()!;
        var signals = config.GetSection("ForexSignals").Get<ForexSignalSettings>()!;
        Assert.Empty(patterns.Validate()); Assert.Empty(aggregates.GetValidationErrors());
        Assert.Empty(signals.Validate(trading, aggregates, patterns));
        Assert.All(signals.Profiles, x => { Assert.Null(x.ApprovedFeatureVersion); Assert.Null(x.ApprovedPatternVersion); Assert.Null(x.ApprovedSignalVersion); });
        var research = config.GetSection("ForexResearch").Get<ForexResearchSettings>()!;
        Assert.False(research.AutomaticPaperSignalsEnabled); Assert.Empty(research.Profiles);
        Assert.Empty(config.GetSection("ForexExecution").Get<ForexExecutionSettings>()!.Profiles);
        Assert.True(config.GetSection("MarketDataCollection").Get<MarketDataCollectionSettings>()!.Enabled);
        Assert.True(config.GetSection("DatasetStorage").Get<DatasetStorageSettings>()!.Enabled);
    }

    [Theory]
    [InlineData("SPY")]
    [InlineData("EURUSD")]
    public async Task CollectionConfigurationBlocksAllRealAdapterOrderMutations(string symbol)
    {
        var config = Config();
        var services = new ServiceCollection().AddLogging();
        services.AddOptions<TradingSettings>().Bind(config.GetSection("TradingSettings"));
        services.AddOptions<IbkrSettings>().Bind(config.GetSection("IbkrSettings"));
        services.AddInfrastructure();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IbkrOptions>();
        Assert.True(options.ReadOnlyDiagnostics); Assert.Equal(7497, options.Port); Assert.Equal(96, options.ClientId);
        var broker = provider.GetRequiredService<IbkrBrokerService>();
        var order = new OrderRequestDto { Symbol = symbol, Quantity = 1, Side = "BUY", Type = "MARKET" };
        Assert.Contains("read-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => broker.SubmitOrderAsync(order))).Message);
        Assert.Contains("read-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ModifyOrderAsync("1", order))).Message);
        Assert.Contains("read-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => broker.CancelOrderAsync("1"))).Message);
    }

    [Fact]
    public async Task CollectionProfileInitializesOperationalSchemaAndResumesSameBackfillWithoutOrders()
    {
        using var f = new ForexResearchFixture();
        var config = Config();
        var trading = config.GetSection("TradingSettings").Get<TradingSettings>()!;
        await using (var db = f.CreateDbContext()) await db.Database.EnsureDeletedAsync();
        using var provider = new ServiceCollection().AddSingleton<IDbContextFactory<TradingBotDbContext>>(f).BuildServiceProvider();
        var migration = new MigrationHostedService(provider, NullLogger<MigrationHostedService>.Instance);
        var registry = new InstrumentRegistryService(f, Options.Create(trading), f);
        var settings = config.GetSection("HistoricalBackfill").Get<HistoricalBackfillSettings>()!;
        Assert.Empty(settings.GetValidationErrors());
        var backfill = new HistoricalBackfillService(f, new FakeMarketDataService(), registry, Options.Create(settings), f,
            NullLogger<HistoricalBackfillService>.Instance);
        for (var restart = 0; restart < 2; restart++)
        {
            await migration.StartAsync(default); await registry.SynchronizeConfiguredAsync(); await backfill.SynchronizePlanAsync();
        }
        await using var check = f.CreateDbContext();
        Assert.Equal(2, await check.InstrumentRegistryRecords.CountAsync());
        var job = Assert.Single(await check.HistoricalBackfillJobRecords.ToArrayAsync());
        Assert.Equal("FX-CASH-EUR-USD-IDEALPRO", job.InstrumentId); Assert.Equal("1m", job.Timeframe);
        Assert.Equal(TimeSpan.FromDays(3), job.DesiredEndUtc - job.DesiredStartUtc);
        Assert.Empty(await check.OrderRecords.ToArrayAsync()); Assert.Empty(await check.ExecutionRecords.ToArrayAsync());
        Assert.Empty(await check.PortfolioRiskReservationRecords.ToArrayAsync());
        Assert.Empty(await check.NumericalModelRecords.ToArrayAsync());
        Assert.All(await registry.GetAllAsync(), x => Assert.False(x.CanSubmitPaperOrders));

        // Exercise the complete operational schema, not only tables touched by collection.
        await check.Database.OpenConnectionAsync();
        foreach (var entity in check.Model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            var columns = entity.GetProperties().Select(x => "t.\"" + x.GetColumnName(table)!.Replace("\"", "\"\"") + "\"");
            await using var command = check.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT " + string.Join(",", columns) + " FROM \"" + table.Name.Replace("\"", "\"\"") + "\" AS t LIMIT 0";
            await using var reader = await command.ExecuteReaderAsync();
        }
        check.InstrumentRolloutEvaluationRecords.Add(new() { InstrumentId = "preserved-proof", EvaluatedAtUtc = f.UtcNow });
        await check.SaveChangesAsync();
        // Simulate a legacy database that already contains rollout data, but no repair marker.
        await check.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20261025193000_EnsureOperationalEvidenceTables'");
        await migration.StartAsync(default);
        Assert.Equal("preserved-proof", (await check.InstrumentRolloutEvaluationRecords.SingleAsync()).InstrumentId);
    }
}
