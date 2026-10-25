using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexModelDecisionTests
{
    internal static PatternCandidate Candidate(ForexResearchContract contract, DateTime now, string strategy = "patterns", TradeDirection direction = TradeDirection.Long)
    {
        var c = contract.Get("EURUSD", strategy);
        return new(PatternType.Hammer, "EURUSD", Timeframe.FiveSeconds, now, .7m, metadata: new Dictionary<string, string>()
            { ["decisionAtUtc"] = now.ToString("O"), ["atrToPriceRatio"] = "0.002" },
            context: PipelineContext.CreateForSignal("EURUSD", "fx10-test", strategy, c.Feature, c.Pattern), direction: direction);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("short")]
    [InlineData("stale")]
    [InlineData("horizon")]
    [InlineData("exit")]
    [InlineData("cost")]
    [InlineData("missingForecast")]
    public async Task ProposalRequiresPriceFeaturesForecastAndMatchingExitPolicy(string scenario)
    {
        using var f = new ForexResearchFixture(); f.UtcNow = ForexResearchFixture.Start;
        var latest = new LatestMarketDataService(Options.Create(f.Aggregates), Options.Create(f.Trading));
        foreach (var kind in new[] { MarketDataEventKind.Bid, MarketDataEventKind.Ask }) latest.Apply(new()
        { EventId = "IBKR:FXQ:test:1:" + kind.ToString().ToLowerInvariant(), InstrumentId = "EURUSD", Symbol = "EURUSD", Kind = kind,
            EventTimeUtc = f.UtcNow, ReceivedTimeUtc = f.UtcNow, Source = ForexQuoteAggregator.QuoteSource, IsFinal = true,
            Price = kind == MarketDataEventKind.Bid ? 1.1m : 1.10002m });
        var model = new ForexPredictionStub { Horizon = 10 };
        var exit = new ExitStrategySettings(); var contract = f.Contract();
        var candidate = Candidate(contract, f.UtcNow, direction: scenario == "short" ? TradeDirection.Short : TradeDirection.Long);
        if (scenario == "stale") f.UtcNow = f.UtcNow.AddSeconds(4);
        if (scenario == "horizon") model.Horizon = 60;
        if (scenario == "exit") exit.Mode = ExitStrategyMode.AtrTrailing;
        if (scenario == "cost") model.Gross = .01m;
        if (scenario == "missingForecast") model.Gross = null;
        var service = new ForexModelDecisionService(contract, model, latest, Options.Create(f.Trading), Options.Create(exit), Options.Create(new ForexExecutionSettings()), f);
        if (scenario != "valid") { await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateProposalAsync(candidate)); return; }
        var proposal = await service.CreateProposalAsync(candidate);
        Assert.Null(proposal.RiskDecision); Assert.Null(proposal.ArbitrationId); // must still pass FX-9
        Assert.Equal(.1m, proposal.AiAnalysis.ExpectedMovePercent); Assert.Equal(0, proposal.AiAnalysis.ExpectedHorizonMinutes);
        Assert.Equal(candidate.Context.SignalId, proposal.Context.SignalId);
        Assert.Contains(ForexResearchContract.Model, proposal.Context.StrategyVersion);
        Assert.Equal(0, model.Last!.NormalizedLiquidity); Assert.Equal(.002m, model.Last.NormalizedVolatility);
        Assert.Equal(contract.Get("EURUSD", "patterns").Label, model.Last.ExecutionLabelVersion);
        Assert.False(f.Trading.Instruments[0].TradingEnabled);
    }
}

internal sealed class ForexPredictionStub : INumericalModelService
{
    public int Horizon = 60; public decimal? Gross = 10; public NumericalModelPredictionRequest? Last;
    public Task<NumericalModelPrediction> PredictAsync(NumericalModelPredictionRequest request, CancellationToken cancellationToken = default)
    { Last = request; return Task.FromResult(new NumericalModelPrediction(Guid.Parse("00000000-0000-0000-0000-000000000010"), 1, .9m, .6m, 5, Horizon, true, "Synthetic test forecast") { ExpectedGrossReturnBps = Gross }); }
    public Task<NumericalModelSnapshot> TrainAsync(NumericalModelTrainingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<NumericalModelSnapshot> DecideAsync(Guid id, NumericalModelDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<NumericalModelSnapshot> RollbackAsync(Guid id, NumericalModelRollbackRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<NumericalModelDecisionSnapshot>> GetDecisionHistoryAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<NumericalModelSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<NumericalModelSnapshot>> GetAllAsync(int count = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
