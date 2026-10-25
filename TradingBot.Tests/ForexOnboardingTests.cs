using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class ForexOnboardingTests
{
    [Fact]
    public async Task FreshOperationalStartupAndRestartPreserveMixedRegistryAndPlanBackfillWithTradingOff()
    {
        using var f = new ForexResearchFixture();
        await using (var db = f.CreateDbContext()) await db.Database.EnsureDeletedAsync();
        using var provider = new ServiceCollection().AddSingleton<IDbContextFactory<TradingBotDbContext>>(f).BuildServiceProvider();
        var migration = new MigrationHostedService(provider, NullLogger<MigrationHostedService>.Instance);
        await migration.StartAsync(default);
        f.Trading.Instruments = [f.Trading.Instruments[0], new() { Symbol = "SPY", InstrumentId = "SPY", Enabled = true, TradingEnabled = false }];
        var registry = new InstrumentRegistryService(f, Options.Create(f.Trading), f);
        await new InstrumentRegistryHostedService(registry, NullLogger<InstrumentRegistryHostedService>.Instance).StartAsync(default);
        var backfill = new HistoricalBackfillService(f, new FakeMarketDataService(), registry, Options.Create(new HistoricalBackfillSettings()), f, NullLogger<HistoricalBackfillService>.Instance);
        await backfill.SynchronizePlanAsync();
        await migration.StartAsync(default);
        await registry.SynchronizeConfiguredAsync(); await backfill.SynchronizePlanAsync();
        await using var check = f.CreateDbContext();
        Assert.Equal(2, await check.InstrumentRegistryRecords.CountAsync());
        Assert.Equal(6, await check.HistoricalBackfillJobRecords.CountAsync());
        Assert.Empty(await check.OrderRecords.ToArrayAsync()); // Materializes every operational column, including compatibility repairs.
        Assert.Empty(await check.ExecutionRecords.ToArrayAsync());
        Assert.All(await registry.GetAllAsync(), x => { Assert.True(x.CollectionEnabled); Assert.False(x.CanSubmitPaperOrders); });
    }

    [Fact]
    public async Task AnalysisOnlyHostedSignalsPersistModelObservationsWithoutOrdersAndKeepStockQuotes()
    {
        using var f = new ForexResearchFixture();
        var latest = new LatestMarketDataService(Options.Create(f.Aggregates), Options.Create(f.Trading));
        var inputs = new ForexFeatureService(latest, f.Features, Options.Create(f.Trading), Options.Create(f.Aggregates));
        var trigger = new ForexSignalTriggerService(Options.Create(f.Signals), Options.Create(f.Patterns), Options.Create(f.Trading), Options.Create(f.Aggregates),
            latest, inputs, f.Features, f.Research(), f, NullLoggerFactory.Instance);
        var decision = new ForexModelDecisionService(f.Contract(), new ForexPredictionStub { Horizon = 10 }, latest,
            Options.Create(f.Trading), Options.Create(f.Profile.Exit), Options.Create(new ForexExecutionSettings()), f);
        var shadow = new ForexShadowService(decision, f.Contract(), f, latest, Options.Create(f.Trading), f);
        var connection = new FakeIbkrConnectionService(); await connection.ConnectAsync();
        using var host = new ForexSignalHostedService(trigger, latest, Options.Create(f.Signals), connection, f, NullLogger<ForexSignalHostedService>.Instance, shadow: shadow);
        foreach (var group in ForexResearchFixture.Records().GroupBy(x => x.ReceivedTimeUtc))
        {
            foreach (var row in group) latest.Apply(Event(row));
            f.UtcNow = group.Key; await host.RunOnceAsync(f.UtcNow);
        }
        latest.Apply(new() { InstrumentId = "SPY", Symbol = "SPY", Kind = MarketDataEventKind.Trade, Price = 600, Size = 10,
            EventTimeUtc = f.UtcNow, ReceivedTimeUtc = f.UtcNow, EventId = "stock-1", Source = "IBKR.TickByTick" });
        Assert.Equal(600, latest.Get("SPY")!.LastTrade);
        await using var db = f.CreateDbContext();
        var observations = await db.ScalpingExecutionDecisionRecords.ToArrayAsync();
        Assert.NotEmpty(observations); Assert.Contains(observations, x => x.Approved);
        Assert.All(observations, x => { Assert.Equal("AnalysisOnly", ForexShadowService.ReadProof(x)!.Mode); Assert.True(ForexShadowService.ReadProof(x)!.ElapsedMilliseconds >= 0); });
        Assert.Contains(await db.ResearchCandidateRecords.ToArrayAsync(), x => x.DecisionStage == ForexSignalTriggerService.AcceptedStage);
        Assert.Empty(await db.OrderRecords.ToArrayAsync()); Assert.Empty(await db.PortfolioRiskReservationRecords.ToArrayAsync());
        var before = observations.Length; await host.RunOnceAsync(f.UtcNow);
        Assert.Equal(before, await db.ScalpingExecutionDecisionRecords.CountAsync());
        await connection.DisconnectAsync(); await host.RunOnceAsync(f.UtcNow.AddSeconds(1));
        Assert.Equal(before, await db.ScalpingExecutionDecisionRecords.CountAsync());
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("replay-tamper")]
    [InlineData("model-tamper")]
    [InlineData("evaluation-tamper")]
    [InlineData("stock-label")]
    [InlineData("stale-quote")]
    [InlineData("unapproved-signal")]
    [InlineData("old-shadow")]
    [InlineData("slow-shadow")]
    [InlineData("wrong-model")]
    [InlineData("paper-observation")]
    [InlineData("quote-age")]
    [InlineData("tick-gap")]
    public async Task FxRolloutRequiresCurrentEvidenceAndMeasuredAnalysisOnlySample(string scenario)
    {
        using var f = new ForexResearchFixture(); f.Trading.Instruments[0].MarketDataTimeframes = ["1m"];
        var registry = new InstrumentRegistryService(f, Options.Create(f.Trading), f);
        await registry.SynchronizeConfiguredAsync();
        await f.Dataset.WriteBatchAsync(ForexResearchFixture.Records());
        var replay = await f.Replay().RunForexResearchAsync(new("EURUSD", ForexResearchFixture.Start, ForexResearchFixture.Start.AddSeconds(192), StrategyId: "patterns"));
        await using (var db = f.CreateDbContext()) Assert.Equal(ReplayRunStatus.Completed, (await db.ReplayRunRecords.SingleAsync()).Status);
        var contract = f.Contract(); var c = contract.Get("EURUSD", "patterns");
        var latest = new LatestMarketDataService(Options.Create(f.Aggregates), Options.Create(f.Trading));
        foreach (var row in ForexResearchFixture.Records()) latest.Apply(Event(row));
        f.UtcNow = ForexResearchFixture.Start.AddSeconds(191); latest.AdvanceTo(f.UtcNow);
        await using (var db = f.CreateDbContext())
        {
            var instrument = await db.InstrumentRegistryRecords.SingleAsync(); instrument.Status = InstrumentOnboardingStatus.WarmingUp; instrument.StatusChangedAtUtc = f.UtcNow.AddMinutes(-5);
            db.HistoricalBackfillJobRecords.Add(new() { InstrumentId = "EURUSD", Symbol = "EURUSD", Timeframe = "1m", Status = HistoricalBackfillStatus.Completed,
                DesiredStartUtc = f.UtcNow.AddHours(-1), DesiredEndUtc = f.UtcNow, NextSegmentEndUtc = f.UtcNow.AddHours(-1) });
            db.Candles.Add(new() { InstrumentId = "EURUSD", Symbol = "EURUSD", Timeframe = Timeframe.OneMinute, TimestampUtc = f.UtcNow.AddMinutes(-1),
                IsFinal = true, QualityStatus = MarketDataQualityStatus.Healthy, Source = "IBKR.Midpoint", Volume = null });
            db.MarketDataStreamStateRecords.Add(new() { StreamKey = "EURUSD|BID|", InstrumentId = "EURUSD", IsHealthy = true });
            var evaluation = new ResearchEvaluationRunRecord { Id = Guid.NewGuid(), RunKey = "fx11-test", ReplayRunId = replay.ReplayRunId,
                InstrumentId = "EURUSD", StrategyId = "patterns", HorizonSeconds = 10, FeatureVersion = c.Feature, PatternVersion = c.Pattern, LabelVersion = c.Label,
                EvaluationVersion = ResearchEvaluationVersion.Create(new()), CompletedAtUtc = f.UtcNow, InputSha256 = "synthetic" };
            evaluation.OutputSha256 = ResearchEvaluationService.ComputeOutputHash(evaluation); db.ResearchEvaluationRunRecords.Add(evaluation);
            var model = new NumericalModelRecord { Id = Guid.NewGuid(), ModelVersion = 1, Status = NumericalModelStatus.Approved, EvaluationRunId = evaluation.Id,
                InstrumentId = "EURUSD", StrategyId = "patterns", HorizonSeconds = 10, FeatureVersion = c.Feature, PatternVersion = c.Pattern, LabelVersion = c.Label,
                ModelVersionTag = ForexResearchContract.Model, ApprovalReady = true, ModelArtifact = [1], InputSha256 = "synthetic" };
            model.OutputSha256 = NumericalModelService.OutputHash(model); db.NumericalModelRecords.Add(model);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            model = await db.NumericalModelRecords.SingleAsync(); model.OutputSha256 = NumericalModelService.OutputHash(model); await db.SaveChangesAsync();
            if (scenario == "replay-tamper") (await db.ReplayRunRecords.SingleAsync()).OutputSha256 = "tampered";
            if (scenario == "model-tamper") model.ModelArtifact = [2];
            if (scenario == "evaluation-tamper") (await db.ResearchEvaluationRunRecords.SingleAsync()).OutputSha256 = "tampered";
            if (scenario == "stock-label") foreach (var label in await db.CandidateLabelRecords.ToArrayAsync()) label.LabelVersion = "labels-stock";
            if (scenario == "tick-gap") db.TickCoverageGapRecords.Add(new() { InstrumentId = "EURUSD", StartUtc = f.UtcNow.AddMinutes(-1), EndUtc = f.UtcNow, RecordedAtUtc = f.UtcNow });
            await db.SaveChangesAsync();
        }
        if (scenario == "stale-quote") f.UtcNow = f.UtcNow.AddSeconds(10);
        if (scenario == "unapproved-signal") f.Signals.Profiles[0].ApprovedSignalVersion = "old";
        var opts = new InstrumentRolloutSettings { MinimumFinalBarsPerTimeframe = 1, MinimumCompleteLabelsPerStrategy = 1, MinimumShadowDecisions = 2, MinimumApprovedShadowDecisions = 1 };
        var engine = new TradingEngineStatusService(); engine.SetState(TradingEngineState.Ready, false, "test", reconciliationCompleted: true);
        var fx = new ForexRolloutService(f, registry, contract, new(latest, f.Features, Options.Create(f.Trading), Options.Create(f.Aggregates)), latest, engine,
            Options.Create(opts), Options.Create(new ResearchEvaluationSettings()), Options.Create(new ShadowPerformanceSettings { MinimumDecisionsPerStrategy = 2, MinimumObservationMinutes = 1 }), Options.Create(f.Signals), f);
        var rollout = new InstrumentRolloutService(f, registry, engine, Options.Create(opts), Options.Create(f.Trading), f, forex: fx);
        var first = await rollout.EvaluateAsync("EURUSD", true);
        if (scenario is "replay-tamper" or "stock-label") { Assert.Equal(InstrumentOnboardingStatus.WarmingUp, first.StatusAfter); return; }
        Assert.Equal(InstrumentOnboardingStatus.ResearchReady, first.StatusAfter);
        var second = await rollout.EvaluateAsync("EURUSD", true);
        if (scenario is "model-tamper" or "evaluation-tamper" or "stale-quote" or "unapproved-signal" or "tick-gap")
        { Assert.Equal(InstrumentOnboardingStatus.ResearchReady, second.StatusAfter); return; }
        Assert.True(second.StatusAfter == InstrumentOnboardingStatus.ShadowReady, string.Join("\n", second.Reasons));
        // Advance the test clock and refresh current quotes without pretending historical quotes were received now.
        var shadowStart = f.UtcNow; f.UtcNow = f.UtcNow.AddSeconds(65);
        for (var at = shadowStart; at <= f.UtcNow; at = at.AddSeconds(1))
            foreach (var kind in new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask }) latest.Apply(new()
            { EventId = $"IBKR:FXQ:fx10:fresh-{at.Ticks}:" + kind.ToString().ToLowerInvariant(), InstrumentId = "EURUSD", Symbol = "EURUSD", Kind = kind,
                EventTimeUtc = at, ReceivedTimeUtc = at, Source = ForexQuoteAggregator.QuoteSource, Price = kind == MarketDataEventKind.Bid ? 1.1m : 1.10002m });
        await using (var db = f.CreateDbContext())
        {
            var model = await db.NumericalModelRecords.SingleAsync();
            foreach (var when in new[] { shadowStart.AddSeconds(1), f.UtcNow }) db.ScalpingExecutionDecisionRecords.Add(new()
            { SignalId = Guid.NewGuid(), InstrumentId = "EURUSD", Symbol = "EURUSD", StrategyId = "patterns", Approved = true, SelectedPolicy = ForexShadowService.Policy,
                ExpectedNetEdgeBps = 5,
                EvaluatedAtUtc = scenario == "old-shadow" ? when.AddHours(-1) : when,
                DecisionJson = JsonSerializer.Serialize(new ForexShadowService.Proof(c.Feature, c.Pattern, c.Label,
                    scenario == "wrong-model" ? "old" : $"{ForexResearchContract.Model}:{model.Id}:{model.ModelVersion}",
                    scenario == "paper-observation" ? "PaperTrading" : "AnalysisOnly", scenario == "slow-shadow" ? 5000 : 1, 1, scenario == "quote-age" ? 5000 : 1)) });
            await db.SaveChangesAsync();
        }
        var final = await rollout.EvaluateAsync("EURUSD", true);
        Assert.Equal(scenario == "valid", final.ShadowCriteriaPassed);
        Assert.Equal(scenario == "valid" ? InstrumentOnboardingStatus.PaperReady : InstrumentOnboardingStatus.ShadowReady, final.StatusAfter);
        Assert.False(final.PaperCriteriaPassed); Assert.False(final.EligibleForManualLiveApproval);
        Assert.False((await registry.GetAsync("EURUSD"))!.CanSubmitPaperOrders);
        if (scenario == "valid")
        {
            f.UtcNow = f.UtcNow.AddSeconds(10);
            Assert.True((await rollout.EvaluateAsync("EURUSD", true)).Suspended);
            Assert.True((await registry.GetAsync("EURUSD"))!.CollectionEnabled);
        }
    }

    [Fact]
    public async Task UnitsAndPrecisionRemainDistinctForFxStockAndUnknownHistory()
    {
        using var f = new ForexResearchFixture();
        f.Trading.Instruments = [f.Trading.Instruments[0], new() { InstrumentId = "SPY", Symbol = "SPY", TradingEnabled = false }];
        var registry = new InstrumentRegistryService(f, Options.Create(f.Trading), f); await registry.SynchronizeConfiguredAsync();
        var fx = (await registry.GetAsync("EURUSD"))!; Assert.Equal("EUR/USD", fx.DisplaySymbol); Assert.Equal("EUR", fx.QuantityUnit); Assert.Equal("USD/EUR", fx.PriceUnit);
        await using var db = f.CreateDbContext();
        foreach (var symbol in new[] { "EURUSD", "SPY", "REMOVED" }) db.Trades.Add(new() { Symbol = symbol, Size = 20000, EntryPrice = 1.12345m });
        await db.SaveChangesAsync(); var rows = await new TradeDashboardService(f).GetTradesAsync();
        Assert.Equal("USD/EUR", rows.Single(x => x.Symbol == "EURUSD").PriceUnit); Assert.Equal(1.12345m, rows.Single(x => x.Symbol == "EURUSD").Entry);
        Assert.Equal("shares", rows.Single(x => x.Symbol == "SPY").QuantityUnit); Assert.Equal("unknown", rows.Single(x => x.Symbol == "REMOVED").ProfitCurrency);
        var engine = new TradingEngineStatusService();
        using var provider = new ServiceCollection().AddLogging().AddSingleton<IInstrumentRegistryService>(registry)
            .AddSingleton<ILatestMarketDataService>(new LatestMarketDataService(trading: Options.Create(f.Trading)))
            .AddSingleton<IInstrumentRolloutService>(new InstrumentRolloutService(f, registry, engine, Options.Create(new InstrumentRolloutSettings()), Options.Create(f.Trading), f))
            .BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(provider, provider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TradingBot.Web.Pages.Instruments>()).ToHtmlString());
        Assert.Contains("EUR/USD", html); Assert.Contains("USD/EUR", html); Assert.Contains("shares", html); Assert.Contains("SPY", html);
        Assert.Contains("trade volume/VWAP unavailable", html); Assert.Contains("Readiness does not enable orders", html);
    }

    private static CanonicalMarketDataEvent Event(MarketDatasetRecord row) => new()
    { EventId = row.EventId, InstrumentId = row.InstrumentId, Symbol = row.Symbol, Kind = row.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
        EventTimeUtc = row.EventTimeUtc, ReceivedTimeUtc = row.ReceivedTimeUtc, Price = row.Price, Size = row.Size, Source = row.Source };
}
