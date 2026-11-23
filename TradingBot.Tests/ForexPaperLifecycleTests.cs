using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Background;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;
using TradingBot.Persistence;
using TradingBot.Tests.Fakes;

namespace TradingBot.Tests;

public sealed class ForexPaperLifecycleTests
{
    [Fact]
    public async Task QuotesToFeatureSignalModelRiskFillExitAndPostTradeRetainOneDecisionIdentity()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        using var research = new ForexResearchFixture(); research.Trading = f.Trading; research.Limits = f.Limits;
        research.Costs.StrategyId = research.Profile.StrategyId = research.Patterns.Profiles[0].StrategyId = research.Signals.Profiles[0].Selection.StrategyId = "fx";
        research.Profile.HoldingSeconds = 60; research.Profile.Exit = f.ExitSettings;
        var latest = new LatestMarketDataService(Options.Create(research.Aggregates), Options.Create(f.Trading));
        foreach (var row in ForexResearchFixture.Records()) latest.Apply(new()
        {
            EventId = row.EventId, InstrumentId = row.InstrumentId, Symbol = row.Symbol, Source = row.Source,
            Kind = row.DataType == "bid" ? MarketDataEventKind.Bid : MarketDataEventKind.Ask,
            EventTimeUtc = row.EventTimeUtc, ReceivedTimeUtc = row.ReceivedTimeUtc, Price = row.Price, Size = row.Size
        });
        f.Clock.UtcNow = ForexResearchFixture.Start.AddSeconds(192); latest.AdvanceTo(f.Clock.UtcNow);
        var inputs = new ForexFeatureService(latest, research.Features, Options.Create(f.Trading), Options.Create(research.Aggregates));
        var contract = research.Contract(); var versions = contract.Get("EURUSD", "fx");
        var profile = research.Signals.Profiles[0]; profile.ApprovedFeatureVersion = versions.Feature;
        profile.ApprovedSignalVersion = profile.Version; profile.ApprovedPatternVersion = research.Patterns.Profiles[0].Version;
        var sink = new CandidateResearchService(research.Dataset, f.Factory, f.Clock, Options.Create(new CandidateLabelingSettings()), NullLogger<CandidateResearchService>.Instance);
        var trigger = new ForexSignalTriggerService(Options.Create(research.Signals), Options.Create(research.Patterns), Options.Create(f.Trading),
            Options.Create(research.Aggregates), latest, inputs, research.Features, sink, f.Factory, NullLoggerFactory.Instance);
        var candidate = Assert.Single(await trigger.ProcessAsync("EURUSD", f.Clock.UtcNow));
        Assert.Contains("featureSnapshotJson", candidate.Metadata.Keys);
        var model = new ForexPredictionStub();
        var decision = new ForexModelDecisionService(contract, model, f.Quotes, Options.Create(f.Trading), Options.Create(f.ExitSettings), Options.Create(new ForexExecutionSettings()), f.Clock);
        var plan = await f.Prepare.PrepareAsync(await decision.CreateProposalAsync(candidate));
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, plan.RiskDecision.ApprovedQuantity, 1.10002m);
        await f.Broker.FillOpenOrderAsync((await f.State()).TakeProfitBrokerOrderId, plan.RiskDecision.ApprovedQuantity, 1.104m);
        await f.Broker.PublishPendingCancellationsAsync(); await f.Analysis().SynchronizeBatchAsync();
        using var db = f.Factory.CreateDbContext(); var result = Assert.Single(db.PostTradeAnalysisRecords);
        Assert.Equal(candidate.Context.SignalId, result.SignalId);
        Assert.Equal(candidate.Context.SignalId, (await db.PortfolioRiskReservationRecords.SingleAsync()).SignalId);
        Assert.Contains("featureSnapshotJson", result.EntryContextJson); Assert.Contains("modelInputJson", result.EntryContextJson);
        using var context = System.Text.Json.JsonDocument.Parse(result.EntryContextJson);
        Assert.Equal(candidate.PatternKey, context.RootElement.GetProperty("lifecycleContext").GetProperty("pattern").GetProperty("patternKey").GetString());
        Assert.Equal(2, db.ExecutionRecords.Count()); Assert.True(result.FeesComplete);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ApprovedModelRouteUsesPaperPreparationAndReleasesRejectedQueue(bool acceptQueue)
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        using var research = new ForexResearchFixture(); research.Trading = f.Trading; research.Limits = f.Limits;
        research.Costs.StrategyId = research.Profile.StrategyId = research.Patterns.Profiles[0].StrategyId = research.Signals.Profiles[0].Selection.StrategyId = "fx";
        research.Profile.HoldingSeconds = 60; research.Profile.Exit = f.ExitSettings;
        var model = new ForexPredictionStub(); var contract = research.Contract();
        var candidate = ForexModelDecisionTests.Candidate(contract, f.Clock.UtcNow, "fx");
        var decision = new ForexModelDecisionService(contract, model, f.Quotes, Options.Create(f.Trading), Options.Create(f.ExitSettings),
            Options.Create(new ForexExecutionSettings()), f.Clock);
        var settings = new ForexResearchSettings(); var channel = new TestFxChannel(acceptQueue);
        var router = new ForexModelExecutionService(Options.Create(settings), Options.Create(f.Trading), decision, f.Prepare, channel, f.Portfolio, f.Arbitration);
        await router.ProcessAsync(candidate, default); Assert.Null(model.Last); Assert.Null(channel.Plan);
        settings.AutomaticPaperSignalsEnabled = true;
        if (!acceptQueue)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => router.ProcessAsync(candidate, default));
            Assert.Equal(PortfolioReservationStatus.Released, (await f.Reservation()).Status); return;
        }
        await router.ProcessAsync(candidate, default); Assert.NotNull(channel.Plan);
        Assert.Contains(ForexResearchContract.Model, channel.Plan.Context.StrategyVersion);
        await f.Executor.ExecutePlanAsync(channel.Plan, default);
        var entry = await f.Entry(); Assert.Equal(20000, entry.RequestedQuantity);
        await f.Broker.FillOpenOrderAsync(entry.BrokerOrderId, 20000, 1.10002m);
        Assert.Equal(f.Clock.UtcNow.AddSeconds(60), (await f.State()).ExitDeadlineUtc);
        await f.Broker.FillOpenOrderAsync((await f.State()).ProtectiveStopBrokerOrderId, 20000, 1.099m);
        await f.Broker.PublishPendingCancellationsAsync();
        await f.Analysis().SynchronizeBatchAsync();
        using var completed = f.Factory.CreateDbContext();
        var result = Assert.Single(completed.PostTradeAnalysisRecords);
        Assert.Contains("modelInputJson", result.EntryContextJson);
        Assert.Contains("executionQuoteJson", result.EntryContextJson);
        Assert.Contains("proofs", result.EntryContextJson);
    }

    private sealed class TestFxChannel(bool accept) : ITradePipelineChannel
    {
        public ApprovedTradePlan? Plan;
        public System.Threading.Channels.ChannelReader<ApprovedTradePlan> ApprovedTradePlanReader => throw new NotSupportedException();
        public ValueTask<bool> PublishAsync(ApprovedTradePlan plan, CancellationToken cancellationToken = default) { Plan = plan; return ValueTask.FromResult(accept); }
    }
    [Theory]
    [InlineData(ExitStrategyMode.FixedBracket, "stop")]
    [InlineData(ExitStrategyMode.FixedBracket, "target")]
    [InlineData(ExitStrategyMode.FixedBracket, "timer")]
    [InlineData(ExitStrategyMode.AtrTrailing, "stop")]
    [InlineData(ExitStrategyMode.AtrTrailing, "timer")]
    public async Task BaseUnitEntryFillProtectionExitUsesExistingLifecycle(ExitStrategyMode mode, string exitKind)
    {
        await using var f = await Flow.Create(mode);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        await f.Executor.ExecutePlanAsync(plan, default);
        var entry = await f.Entry();
        Assert.Equal(20000, entry.RequestedQuantity);
        Assert.Equal(PortfolioReservationStatus.Committed, (await f.Reservation()).Status);
        Assert.Equal(DateTime.MaxValue, (await f.Reservation()).ExpiresAtUtc);
        await f.Broker.FillOpenOrderAsync(entry.BrokerOrderId, plan.RiskDecision.ApprovedQuantity, 1.10002m);
        var state = await f.State();
        Assert.Equal(20000, state.ProtectedQuantity);
        Assert.Equal(f.Clock.UtcNow.AddSeconds(60), state.ExitDeadlineUtc);
        var exitId = exitKind == "target" ? state.TakeProfitBrokerOrderId : state.ProtectiveStopBrokerOrderId;
        if (exitKind == "timer")
        {
            f.Clock.UtcNow = state.ExitDeadlineUtc!.Value.AddSeconds(1);
            await f.Exit.ProcessExpiredPositionsAsync(f.Clock.UtcNow);
            await f.Broker.PublishPendingCancellationsAsync();
            exitId = (await f.State()).ExitBrokerOrderId;
        }
        await f.Broker.FillOpenOrderAsync(exitId, plan.RiskDecision.ApprovedQuantity, exitKind == "stop" ? 1.099m : 1.104m);
        await f.Broker.PublishPendingCancellationsAsync();
        Assert.Equal(ExitManagementState.Closed, (await f.State()).State);
        Assert.Equal(PortfolioReservationStatus.Released, (await f.Reservation()).Status);
        Assert.Equal(0, (await f.Broker.GetPositionsAsync("DU123")).Single().Quantity);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(2, await db.ExecutionRecords.CountAsync());
        Assert.Equal(SignalArbitrationStatus.Closed, (await db.SignalArbitrationRecords.SingleAsync()).Status);
        Assert.Single(await db.ForexExecutionProofRecords.ToArrayAsync());
        Assert.True(await f.Analysis().SynchronizeAsync(state.Id));
        Assert.False(await f.Analysis().SynchronizeAsync(state.Id));
        var analysis = Assert.Single(await db.PostTradeAnalysisRecords.ToArrayAsync());
        Assert.Equal(20000m, analysis.Quantity);
        Assert.Equal((exitKind == "stop" ? 1.099m : 1.104m) * 20000m - 1.10002m * 20000m, analysis.GrossProfitLoss);
        Assert.True(analysis.FeesComplete); Assert.Equal(plan.Context.SignalId, analysis.SignalId);
    }

    [Theory]
    [InlineData(ExitStrategyMode.FixedBracket)]
    [InlineData(ExitStrategyMode.AtrTrailing)]
    public async Task PartialFillProtectsOnlyFilledBaseUnitsAndCancelsUnfilledRemainder(ExitStrategyMode mode)
    {
        await using var f = await Flow.Create(mode);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, 1234.56m, 1.10002m);
        var state = await f.State();
        Assert.Equal(1234.56m, state.ProtectedQuantity);
        await f.Broker.FillOpenOrderAsync(state.ProtectiveStopBrokerOrderId, 1234.56m, 1.099m);
        await f.Broker.PublishPendingCancellationsAsync();
        Assert.Equal(ExitManagementState.Closed, (await f.State()).State);
        Assert.Equal(PortfolioReservationStatus.Released, (await f.Reservation()).Status);
        Assert.Equal(0, (await f.Broker.GetPositionsAsync("DU123")).Single().Quantity);
        await f.Analysis().SynchronizeBatchAsync();
        using var complete = f.Factory.CreateDbContext();
        Assert.Equal(1234.56m, Assert.Single(complete.PostTradeAnalysisRecords).Quantity);
    }

    [Fact]
    public async Task AutomaticAnalysisRecoversAfterClosureAndLateFeesUpdateExactlyOneWeightedResult()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        await f.Executor.ExecutePlanAsync(await f.Prepare.PrepareAsync(f.Proposal()), default);
        var buy = await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, 20000m, 1.10002m);
        var state = await f.State();
        var first = await f.Broker.FillOpenOrderAsync(state.TakeProfitBrokerOrderId, 5000m, 1.104m);
        var second = await f.Broker.FillOpenOrderAsync(state.TakeProfitBrokerOrderId, 15000m, 1.105m);
        await f.Broker.PublishPendingCancellationsAsync();
        var committed = new ProjectionLogger();
        var analysisService = new CompletedTradeAnalysisService(f.Factory, f.Clock, committed, Options.Create(f.Trading));
        using (var host = new CompletedTradeAnalysisHostedService(analysisService, NullLogger<CompletedTradeAnalysisHostedService>.Instance))
        {
            await host.StartAsync(default);
            // The fixture shares one in-memory connection; wait for the post-commit signal, not an uncommitted row.
            await committed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync(default);
        }
        using var db = f.Factory.CreateDbContext();
        var before = Assert.Single(await db.PostTradeAnalysisRecords.AsNoTracking().ToArrayAsync());
        Assert.Equal(1.10475m, before.ExitPrice);
        await f.Broker.PublishCommissionAsync(buy.BrokerExecutionId!, 2m);
        await f.Broker.PublishCommissionAsync(first.BrokerExecutionId!, 1m);
        await f.Broker.PublishCommissionAsync(second.BrokerExecutionId!, 2m);
        Assert.True(await f.Analysis().SynchronizeAsync(state.Id));
        Assert.False(await f.Analysis().SynchronizeAsync(state.Id));
        var after = Assert.Single(await db.PostTradeAnalysisRecords.AsNoTracking().ToArrayAsync());
        Assert.Equal(before.Id, after.Id); Assert.Equal(before.GrossProfitLoss - 5m, after.NetProfitLoss);
        await f.Broker.PublishCommissionAsync(second.BrokerExecutionId!, 3m, "EUR");
        await f.Analysis().SynchronizeAsync(state.Id);
        Assert.False((await db.PostTradeAnalysisRecords.AsNoTracking().SingleAsync()).FeesComplete);
        Assert.Equal(0, (await new PostTradeAnalysisService(f.Factory).GetPatternPerformanceAsync(after.Pattern)).Trades);
        Assert.Equal(3, await db.ExecutionRecords.CountAsync());
    }

    private sealed class ProjectionLogger : ILogger<CompletedTradeAnalysisService>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Information) Completed.TrySetResult(); }
    }

    [Fact]
    public async Task AtrTimerUsesFxFeaturesAndObservedBidWithTickRoundedStop()
    {
        await using var f = await Flow.Create(ExitStrategyMode.AtrTrailing);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, 20000, 1.10002m);
        f.Quotes.Bid = 1.103007m;
        f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(1);
        await f.Exit.ProcessExpiredPositionsAsync(f.Clock.UtcNow);
        var state = await f.State();
        Assert.True(state.BreakEvenActivated);
        Assert.True(state.TrailingActivated);
        Assert.True(state.CurrentProtectiveStop > 1.10002m);
        Assert.True(state.CurrentProtectiveStop < f.Quotes.Bid);
        Assert.Equal(0, state.CurrentProtectiveStop % .00001m);
    }

    [Fact]
    public async Task RestartRetainsTimerAndCommittedCapacityUntilConfirmedExit()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, 20000, 1.10002m);
        var deadline = (await f.State()).ExitDeadlineUtc;
        f.Exit.Dispose();
        f.Exit = f.NewExit();
        await f.Exit.RestoreAsync();
        Assert.Equal(deadline, (await f.State()).ExitDeadlineUtc);
        f.Clock.UtcNow = f.Clock.UtcNow.AddHours(1);
        Assert.Single((await f.Portfolio.GetStateAsync("DU123")).ActiveReservations);
        await f.Exit.ProcessExpiredPositionsAsync(f.Clock.UtcNow);
        await f.Exit.ProcessExpiredPositionsAsync(f.Clock.UtcNow);
        await f.Broker.PublishPendingCancellationsAsync();
        Assert.Equal(4, f.Broker.SubmittedRequests.Count);
        await f.Broker.FillOpenOrderAsync((await f.State()).ExitBrokerOrderId, 20000, 1.1005m);
        await f.Broker.ReplayExecutionCallbacksAsync();
        await f.Analysis().SynchronizeBatchAsync();
        await f.Analysis().SynchronizeBatchAsync();
        using var completed = f.Factory.CreateDbContext();
        Assert.Single(completed.PostTradeAnalysisRecords);
        Assert.Equal(PortfolioReservationStatus.Released, (await f.Reservation()).Status);
    }

    [Theory]
    [InlineData(FakeBrokerOrderBehavior.RejectOrder, PortfolioReservationStatus.Released)]
    [InlineData(FakeBrokerOrderBehavior.DisconnectAfterBrokerAcceptedOrder, PortfolioReservationStatus.Committed)]
    public async Task RejectionOrUnknownAcceptancePreservesCorrectReservation(FakeBrokerOrderBehavior behavior, PortfolioReservationStatus expected)
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        f.Broker.Scenario = new() { OrderBehavior = behavior };
        await f.Executor.ExecutePlanAsync(plan, default);
        Assert.Equal(expected, (await f.Reservation()).Status);
        var count = f.Broker.SubmittedRequests.Count;
        await f.Executor.ExecutePlanAsync(plan, default);
        Assert.Equal(count, f.Broker.SubmittedRequests.Count);
        Assert.Equal(expected, (await f.Reservation()).Status);
    }

    [Fact]
    public async Task DuplicatePreparationAndExecutionNeverReleaseActiveExposure()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        var proposal = f.Proposal();
        var plan = await f.Prepare.PrepareAsync(proposal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Prepare.PrepareAsync(proposal));
        Assert.Equal(PortfolioReservationStatus.Pending, (await f.Reservation()).Status);
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Executor.ExecutePlanAsync(plan, default);
        Assert.Equal(3, f.Broker.SubmittedRequests.Count);
        Assert.Equal(PortfolioReservationStatus.Committed, (await f.Reservation()).Status);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("funds")]
    [InlineData("stale")]
    [InlineData("disabled")]
    public async Task FinalRefreshBlocksChangedInputsWithoutSendingOrders(string change)
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        switch (change)
        {
            case "quote": f.Quotes.Bid += .0001m; break;
            case "funds": f.Broker.AvailableFunds = 100; break;
            case "stale": f.Clock.UtcNow = f.Clock.UtcNow.AddSeconds(10); break;
            default: f.Trading.ForexPaperExecutionEnabled = false; break;
        }
        await f.Executor.ExecutePlanAsync(plan, default);
        Assert.Empty(f.Broker.SubmittedRequests);
        Assert.Equal(PortfolioReservationStatus.Released, (await f.Reservation()).Status);
    }

    [Fact]
    public async Task UnknownBrokerOrderBlocksPreparation()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        await f.Broker.SubmitOrderAsync(new() { Symbol = "SPY", Side = "BUY", Type = "LIMIT", Quantity = 1, LimitPrice = 100 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Prepare.PrepareAsync(f.Proposal()));
        await using var db = f.Factory.CreateDbContext();
        Assert.Empty(await db.ForexExecutionProofRecords.ToArrayAsync());
    }

    [Fact]
    public async Task ConcurrentSameSignalHasExactlyOneDurableReservation()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        var proposal = f.Proposal();
        async Task<bool> Try() { try { await f.Prepare.PrepareAsync(proposal); return true; } catch (InvalidOperationException) { return false; } }
        var results = await Task.WhenAll(Try(), Try());
        Assert.Single(results, x => x);
        Assert.Equal(PortfolioReservationStatus.Pending, (await f.Reservation()).Status);
        await using var db = f.Factory.CreateDbContext();
        Assert.Single(await db.ForexExecutionProofRecords.ToArrayAsync());
    }

    [Fact]
    public async Task StockReservationConsumesSameDollarBudgetAndIsPreservedWhenFxIsSized()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        f.Trading.Instruments = [.. f.Trading.Instruments, new() { InstrumentId = "SPY", Symbol = "SPY", TradingEnabled = false }];
        f.Broker.AvailableFunds = 29000;
        await using var db = f.Factory.CreateDbContext();
        db.PortfolioRiskReservationRecords.Add(new() { Id = Guid.NewGuid(), ReservationKey = "stock", AccountId = "DU123", Symbol = "SPY",
            InstrumentId = "SPY", StrategyId = "stock", Quantity = 100, PositionValue = 24000, SignedExposure = 24000,
            Status = PortfolioReservationStatus.Pending, ExpiresAtUtc = f.Clock.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        // FX sizing may shrink to the remaining budget, but must never reserve the original 22k USD.
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        Assert.True(plan.RiskDecision.PositionValue <= 5000);
        Assert.Equal(PortfolioReservationStatus.Pending, (await db.PortfolioRiskReservationRecords.SingleAsync(x => x.ReservationKey == "stock")).Status);
        Assert.Empty(f.Broker.SubmittedRequests);
    }

    [Fact]
    public async Task DailyLossUsesIndividualExitFillsAndAllCommissions()
    {
        await using var f = await Flow.Create(ExitStrategyMode.FixedBracket);
        f.Broker.Scenario = new() { CommissionPerFill = 2m };
        var plan = await f.Prepare.PrepareAsync(f.Proposal());
        await f.Executor.ExecutePlanAsync(plan, default);
        await f.Broker.FillOpenOrderAsync((await f.Entry()).BrokerOrderId, 20000, 1.10002m);
        var stop = (await f.State()).ProtectiveStopBrokerOrderId;
        await f.Broker.FillOpenOrderAsync(stop, 10000, 1.099m);
        await f.Broker.PublishPendingCancellationsAsync();
        await f.Broker.FillOpenOrderAsync(stop, 10000, 1.101m);
        await f.Broker.PublishPendingCancellationsAsync();
        var next = await f.Prepare.PrepareAsync(f.Proposal());
        await using var db = f.Factory.CreateDbContext();
        var proof = await db.ForexExecutionProofRecords.SingleAsync(x => x.ReservationId == next.RiskDecision.ReservationId);
        var request = System.Text.Json.JsonSerializer.Deserialize<ForexRiskRequest>(proof.RequestJson)!;
        Assert.Equal(-6.4m, request.Portfolio.DailyRealizedPnl.Amount); // -0.4 gross, three 2 USD fees
    }

    private sealed class Flow : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public readonly TestClock Clock = new();
        public readonly TradingSettings Trading = new() { Enabled = true, OperatingMode = TradingOperatingMode.PaperTrading, ForexPaperExecutionEnabled = true,
            Instruments = [new() { InstrumentId = "EURUSD", Symbol = "EURUSD", BaseCurrency = "EUR", Currency = "USD", SecurityType = "CASH", Exchange = "IDEALPRO",
                Enabled = true, TradingEnabled = true, StrategyIds = ["fx"], MaximumHoldingSeconds = 60, MarketDataTimeframes = ["1m"] }] };
        public readonly RiskSettings Limits = new() { MaximumPositionValue = 100000, MaximumRiskPerTrade = 1000, MaximumGrossExposure = 200000,
            MaximumNetExposure = 200000, MaximumInstrumentExposure = 100000, MaximumStrategyExposure = 100000, MaximumOpenPositions = 20,
            MaximumCorrelationGroupExposure = 100000, InstrumentCooldownSeconds = 0 };
        public readonly IDbContextFactory<TradingBotDbContext> Factory;
        public readonly DeterministicFakeIbkrBroker Broker;
        public readonly PortfolioRiskService Portfolio;
        public readonly ForexPaperPreparationService Prepare;
        public readonly ApprovedOrderExecutionBackgroundService Executor;
        public readonly Quotes Quotes;
        private readonly OrderManager _orders;
        private readonly SignalArbitrationService _arbitration;
        private readonly ServiceProvider _services;
        private readonly OperatingModeService _mode;
        private readonly TradingEngineStatusService _status = new();
        private readonly FeatureEngine _features = new();
        private readonly ExitStrategySettings _exitSettings;
        public ExitStrategySettings ExitSettings => _exitSettings;
        public SignalArbitrationService Arbitration => _arbitration;
        public ExitManagementService Exit;
        public CompletedTradeAnalysisService Analysis() => new(Factory, Clock, NullLogger<CompletedTradeAnalysisService>.Instance, Options.Create(Trading));

        private Flow(SqliteConnection connection, IDbContextFactory<TradingBotDbContext> factory, ExitStrategyMode exitMode)
        {
            _connection = connection; Factory = factory;
            Broker = new("DU123", true, Clock) { DeferCancellationCallbacks = true };
            Quotes = new(Clock);
            _status.SetState(TradingEngineState.Ready, true, "fake paper", brokerEnvironmentVerification: BrokerEnvironmentVerificationStatus.VerifiedPaper, reconciliationCompleted: true);
            var registry = new InstrumentRegistryService(factory, Options.Create(Trading), Clock);
            _mode = new(Options.Create(Trading), factory, NullLogger<OperatingModeService>.Instance);
            Portfolio = new(factory, Options.Create(Limits), Clock, NullLogger<PortfolioRiskService>.Instance);
            _arbitration = new(factory, Portfolio, Broker, Options.Create(new SignalArbitrationSettings()), Clock, NullLogger<SignalArbitrationService>.Instance);
            var guard = new TradingExecutionGuard(Options.Create(Trading), _mode, _status, registry);
            _orders = new(Broker, guard, factory, NullLogger<OrderManager>.Instance);
            var execution = Options.Create(new ForexExecutionSettings { Profiles = [new() { InstrumentId = "EURUSD", TrailingIntervalSeconds = 5 }] });
            var risk = new ForexRiskService(Options.Create(new ForexRiskSettings { Profiles = [new() { InstrumentId = "EURUSD", StrategyId = "fx",
                MaximumCurrencyExposureUsd = 100000, SafetyBufferBps = 0, SlippagePerSidePips = 0 }] }), Options.Create(Limits), Options.Create(Trading));
            Prepare = new(Options.Create(Trading), Options.Create(new IbkrSettings { PaperAccountId = "DU123" }), Options.Create(Limits), execution,
                _mode, _status, registry, Broker, Broker, Broker, new Rules(Clock), Quotes, risk, Portfolio, _arbitration, factory, Clock);
            var featureService = new ForexFeatureService(Quotes, _features, Options.Create(Trading), Options.Create(new ShortAggregateSettings()));
            _services = new ServiceCollection().AddSingleton<IOrderManager>(_orders).AddSingleton<IOrderModificationService>(Broker)
                .AddSingleton<IIbkrConnectionService>(Broker).AddSingleton(featureService).AddSingleton(execution).BuildServiceProvider();
            _exitSettings = new() { Mode = exitMode, MaximumHoldingSeconds = 60, BreakEvenTriggerR = .5m, TrailingActivationR = 1m };
            Exit = NewExit();
            Executor = new(new TradePipelineChannel(), _orders, _status, _mode, Exit, null, factory, Options.Create(_exitSettings), Options.Create(Trading),
                NullLogger<ApprovedOrderExecutionBackgroundService>.Instance, Portfolio, _arbitration,
                new ScalpingExecutionGate(Quotes, _mode, Broker, Broker, Broker, Portfolio, factory, Options.Create(new ScalpingExecutionSettings()),
                    Options.Create(Trading), Options.Create(Limits), Options.Create(new IbkrSettings { PaperAccountId = "DU123" }),
                    Clock, NullLogger<ScalpingExecutionGate>.Instance, Prepare));
        }
        public ExitManagementService NewExit() => new(Broker, _services, Factory, _mode, _status, _features,
            Options.Create(_exitSettings), Options.Create(Trading), NullLogger<ExitManagementService>.Instance, _arbitration, Clock);
        public static async Task<Flow> Create(ExitStrategyMode mode)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var factory = new DbFactory(new DbContextOptionsBuilder<TradingBotDbContext>().UseSqlite(connection).Options);
            await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var f = new Flow(connection, factory, mode);
            await f.Broker.ConnectAsync();
            var registry = new InstrumentRegistryService(factory, Options.Create(f.Trading), f.Clock);
            await registry.SynchronizeConfiguredAsync();
            await using (var db = factory.CreateDbContext())
            {
                var record = await db.InstrumentRegistryRecords.SingleAsync(); record.Status = InstrumentOnboardingStatus.PaperReady;
                await db.SaveChangesAsync(); // fixture readiness only, not a production onboarding bypass
            }
            return f;
        }
        public ApprovedTradePlan Proposal()
        {
            var context = new PipelineContext(Guid.NewGuid(), Guid.NewGuid(), "EURUSD", "fx", "test", "features-fx-midpoint-v1+test",
                PipelineContractVersions.ForexPatterns + "+test", "test");
            return new() { Pattern = new(PatternType.Hammer, "EURUSD", Timeframe.FiveSeconds, Clock.UtcNow, .9m, context: context),
                AiAnalysis = new() { Action = AiMarketActions.Buy, ExpectedMovePercent = .1m },
                StrategyDecision = new() { Symbol = "EURUSD", Approved = true, Context = context, EntryMin = 1.1m, EntryMax = 1.1001m,
                    StopPrice = 1.099m, TakeProfitPrice = 1.104m, EvaluatedAtUtc = Clock.UtcNow,
                    TradeSignal = new("EURUSD", Timeframe.FiveSeconds, TradeAction.Enter, .9m, 20000, Clock.UtcNow) } };
        }
        public async Task<OrderRecord> Entry() { await using var db = Factory.CreateDbContext(); return await db.OrderRecords.SingleAsync(x => x.Side == "Buy"); }
        public async Task<ExitManagementRecord> State() { await using var db = Factory.CreateDbContext(); return await db.ExitManagementRecords.SingleAsync(); }
        public async Task<PortfolioRiskReservationRecord> Reservation() { await using var db = Factory.CreateDbContext(); return await db.PortfolioRiskReservationRecords.SingleAsync(); }
        public async ValueTask DisposeAsync() { Exit.Dispose(); _orders.Dispose(); _arbitration.Dispose(); _services.Dispose(); await _connection.DisposeAsync(); }
    }

    private sealed class TestClock : IClock { public DateTime UtcNow { get; set; } = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc); }
    private sealed class DbFactory(DbContextOptions<TradingBotDbContext> options) : IDbContextFactory<TradingBotDbContext>
    { public TradingBotDbContext CreateDbContext() => new(options); public Task<TradingBotDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext()); }
    private sealed class Rules(TestClock clock) : IForexBrokerRules
    {
        public Task<ForexBrokerRulesSnapshot> GetForexOrderRulesAsync(ConfiguredInstrument instrument, CancellationToken cancellationToken = default) => Task.FromResult(
            new ForexBrokerRulesSnapshot(new("Confirmed", 120) { MatchCount = 1, Metadata = new(new("EUR", "CASH", "IDEALPRO", "USD"),
                "EUR.USD", "IDEALPRO", .00001m, .01m, .01m, .01m, "42", "UTC", null, null) }, clock.UtcNow,
                new(120, clock.UtcNow, "synthetic paper rules", .01m, 1000000m, .01m, 42, [new(0, .00001m)])));
    }
    private sealed class Quotes(TestClock clock) : ILatestMarketDataService
    {
        public decimal Bid = 1.1m;
        public void Apply(CanonicalMarketDataEvent marketEvent) { }
        public void AdvanceTo(DateTime receivedTimeUtc) { }
        public IReadOnlyList<ShortIntervalBarSnapshot> GetCompletedShortAggregates(string instrumentId, int intervalSeconds, DateTime asOfUtc, int count = 100) => [];
        public LatestMarketDataSnapshot? Get(string instrumentIdOrSymbol) => new() { Bid = Bid, Ask = Bid + .00002m, BidTimeUtc = clock.UtcNow, AskTimeUtc = clock.UtcNow };
        public IReadOnlyList<LatestMarketDataSnapshot> GetAll() => [];
        public IReadOnlyList<ForexQuoteBar> GetCompletedForexAggregates(string instrumentId, int intervalSeconds, DateTime asOfUtc, int count = 100) =>
            Enumerable.Range(0, 40).Select(i => new ForexQuoteBar { Version = "synthetic", IntervalSeconds = intervalSeconds,
                WindowStartUtc = clock.UtcNow.AddSeconds((i - 40) * intervalSeconds - 2), WindowEndUtc = clock.UtcNow.AddSeconds((i - 39) * intervalSeconds - 2),
                AvailableAtUtc = clock.UtcNow.AddSeconds((i - 39) * intervalSeconds), LastQuoteTimeUtc = clock.UtcNow.AddSeconds((i - 39) * intervalSeconds - 3),
                Open = Bid, High = Bid + .0002m, Low = Bid - .0002m, Close = Bid + .00001m, LastBid = Bid, LastAsk = Bid + .00002m,
                MeanSpread = .00002m, MaximumSpread = .00002m, QuoteCount = 3 }).TakeLast(count).ToArray();
    }
}
