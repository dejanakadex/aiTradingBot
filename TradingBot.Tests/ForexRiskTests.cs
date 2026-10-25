using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ForexRiskTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);
    private readonly ForexRiskProfile _profile = new()
    {
        InstrumentId = "EURUSD", StrategyId = "patterns", MaximumCurrencyExposureUsd = 100000m,
        SafetyBufferBps = 0, SlippagePerSidePips = 0, MinimumNetEdgeBps = 0
    };
    private readonly RiskSettings _risk = new()
    {
        MaximumPositionValue = 100000m, MaximumRiskPerTrade = 1000m, MaximumGrossExposure = 200000m,
        MaximumNetExposure = 200000m, MaximumInstrumentExposure = 100000m, MaximumStrategyExposure = 100000m,
        MaximumLeverage = 1m, MaximumOpenPositions = 10, MaximumCorrelationGroupExposure = 100000m
    };

    private TradingSettings Trading(string quote = "USD") => new()
    {
        Instruments = [new() { InstrumentId = "EUR" + quote, Symbol = "EUR" + quote, SecurityType = "CASH",
            Exchange = "IDEALPRO", BaseCurrency = "EUR", Currency = quote, Enabled = true, TradingEnabled = false,
            StrategyIds = ["patterns"], AllowedDirections = [TradeDirection.Long, TradeDirection.Short] }]
    };
    private ForexRiskService Service(string quote = "USD") => new(Options.Create(new ForexRiskSettings { Profiles = [_profile] }),
        Options.Create(_risk), Options.Create(Trading(quote)));
    private static ForexRiskRequest Request() => new("EURUSD", "patterns", TradeDirection.Long, Now,
        1.10000m, 1.10002m, Now, Now, 1.099m, 1.102m, 0.001m, 20000m,
        new IbkrContractProbe("Confirmed", 120) { MatchCount = 1, Metadata = new(new("EUR", "CASH", "IDEALPRO", "USD"),
            "EUR.USD", "IDEALPRO", 0.00001m, 1000m, 1000m, 10000m, "42", "UTC", null, null) }, Now,
        new(120, Now, "synthetic test broker rules", 1000m, 1000000m, 1000m, 42, [new(0, 0.00001m)]),
        new(Now, true, new(100000m, "USD"), new(100000m, "USD"), new(0, "USD"), [], 0), []);

    [Fact]
    public void BaseUnitsPipValueRoundTripFeesAndSignedLegsAreExplicit()
    {
        var result = Service().Evaluate(Request());
        Assert.True(result.Passed, string.Join(" ", result.Reasons));
        Assert.False(result.ExecutionAuthorized);
        Assert.Equal(20000m, result.BaseUnits);
        Assert.Equal(22000.4m, result.NotionalUsd);
        Assert.Equal(2m, result.PipValueUsd);
        Assert.Equal(4m, result.CommissionUsd);
        Assert.Equal(0.4m, result.SpreadUsd);
        Assert.Equal(24.4m, result.StopRiskUsd);
        Assert.Equal(20m, result.ExpectedGrossProfitUsd);
        Assert.Equal(15.6m, result.ExpectedNetProfitUsd);
        Assert.Equal(result.NotionalUsd, result.CurrencyLegsUsd["EUR"]);
        Assert.Equal(-result.NotionalUsd, result.CurrencyLegsUsd["USD"]);
    }

    [Fact]
    public void StopRiskIncludesFixedFeesAndRoundsQuantityDown()
    {
        _risk.MaximumRiskPerTrade = 10m;
        var result = Service().Evaluate(Request());
        Assert.Equal(5000m, result.BaseUnits);
        Assert.Equal(9.1m, result.StopRiskUsd);
        Assert.True(result.Passed);
    }

    [Fact]
    public void HundredDollarAccountCannotBypassMinimumOrLeverage()
    {
        var r = Request();
        r = r with { Portfolio = r.Portfolio with { NetLiquidation = new(100m, "USD"), AvailableBuyingPower = new(100000m, "USD") } };
        var result = Service().Evaluate(r);
        Assert.False(result.Passed);
        Assert.Equal(0, result.BaseUnits);
        Assert.Contains("minimum", Assert.Single(result.Reasons));
    }

    [Fact]
    public void SmallOddLotStillNeedsEdgeToCoverMinimumFees()
    {
        var r = Request();
        r = r with { MaximumBaseUnits = 90, Contract = r.Contract with { Metadata = r.Contract.Metadata! with { MinimumSize = 1, SizeIncrement = 1 } },
            Rules = r.Rules! with { MinimumBaseUnits = 1, BaseUnitIncrement = 1 },
            Portfolio = r.Portfolio with { NetLiquidation = new(100, "USD"), AvailableBuyingPower = new(100, "USD") } };
        var result = Service().Evaluate(r);
        Assert.False(result.Passed);
        Assert.True(result.BaseUnits > 0);
        Assert.Equal(4, result.CommissionUsd);
        Assert.Contains("edge", Assert.Single(result.Reasons));
    }

    [Fact]
    public void BuyingPowerIncludesFeesAndNeverRoundsUp()
    {
        var r = Request();
        var result = Service().Evaluate(r with { Portfolio = r.Portfolio with { AvailableBuyingPower = new(2204m, "USD") } });
        Assert.Equal(1000m, result.BaseUnits); // 2000 units cost 2200.04 + 4, just above available.
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("receive")]
    [InlineData("account")]
    [InlineData("contract")]
    [InlineData("rules")]
    [InlineData("future")]
    public void StaleOrFutureInputsFailClosed(string input)
    {
        var r = Request();
        r = input switch
        {
            "quote" => r with { QuoteEventUtc = Now.AddSeconds(-6) },
            "receive" => r with { QuoteReceivedUtc = Now.AddSeconds(-6) },
            "account" => r with { Portfolio = r.Portfolio with { AsOfUtc = Now.AddSeconds(-6) } },
            "contract" => r with { ContractObservedUtc = Now.AddDays(-2) },
            "rules" => r with { Rules = r.Rules! with { VerifiedAtUtc = Now.AddDays(-2) } },
            _ => r with { QuoteReceivedUtc = Now.AddTicks(1) }
        };
        Assert.False(Service().Evaluate(r).Passed);
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("increment")]
    [InlineData("tick")]
    [InlineData("marketRule")]
    [InlineData("contract")]
    [InlineData("missingRules")]
    public void UnknownOrMismatchedBrokerRulesAreNotGuessed(string input)
    {
        var r = Request();
        r = input switch
        {
            "minimum" => r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { MinimumSize = null } } },
            "increment" => r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { SizeIncrement = null } } },
            "tick" => r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { MinimumTick = null } } },
            "marketRule" => r with { Rules = r.Rules! with { MarketRuleId = 99 } },
            "contract" => r with { Rules = r.Rules! with { ContractId = 999 } },
            _ => r with { Rules = null }
        };
        Assert.False(Service().Evaluate(r).Passed);
    }

    [Fact]
    public void PriceBandCrossingUsesNextIncrementAndConservativeStops()
    {
        var r = Request() with { Ask = 1.10003m, StopPrice = 1.099999m, TargetPrice = 1.10207m };
        r = r with { Rules = r.Rules! with { PriceBands = [new(0, 0.00001m), new(1.10004m, 0.0001m)] } };
        var result = Service().Evaluate(r);
        Assert.Equal(1.10003m, result.EntryPrice);
        Assert.Equal(1.09999m, result.StopPrice);
        Assert.Equal(1.10204m, result.TargetPrice);
        r = r with { Ask = 1.100035m };
        Assert.Equal(1.10004m, Service().Evaluate(r).EntryPrice);
    }

    [Fact]
    public void ShortResearchUsesBidEntryAndReversedCurrencyLegs()
    {
        var result = Service().Evaluate(Request() with { Direction = TradeDirection.Short, StopPrice = 1.101005m, TargetPrice = 1.098005m });
        Assert.True(result.Passed);
        Assert.False(result.ExecutionAuthorized);
        Assert.Equal(1.10000m, result.EntryPrice);
        Assert.Equal(1.10101m, result.StopPrice);
        Assert.Equal(1.09801m, result.TargetPrice);
        Assert.True(result.CurrencyLegsUsd["EUR"] < 0);
        Assert.True(result.CurrencyLegsUsd["USD"] > 0);
    }

    private ForexRiskRequest CrossRequest()
    {
        _profile.InstrumentId = "EURGBP";
        var r = Request() with { InstrumentId = "EURGBP", Bid = .85000m, Ask = .85002m, StopPrice = .849m, TargetPrice = .852m };
        return r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { Contract = new("EUR", "CASH", "IDEALPRO", "GBP") } },
            Conversions = [new("GBP", 1.25m, 1.26m, Now, Now)] };
    }

    [Fact]
    public void CrossPairValuesCostsAtAskAndExpectedProfitAtBidInUsd()
    {
        var r = CrossRequest();
        var result = Service("GBP").Evaluate(r);
        Assert.True(result.Passed);
        Assert.Equal(20000m * .85002m * 1.26m, result.NotionalUsd);
        Assert.Equal(20000m * .0001m * 1.26m, result.PipValueUsd);
        Assert.Equal(25m, result.ExpectedGrossProfitUsd);
        Assert.Equal(.504m, result.SpreadUsd);
        Assert.Equal(29.704m, result.StopRiskUsd);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("crossed")]
    public void InvalidCrossConversionCannotApprove(string kind)
    {
        var r = CrossRequest();
        r = r with { Conversions = kind switch
        {
            "missing" => [], "stale" => [r.Conversions[0] with { EventUtc = Now.AddSeconds(-6) }],
            "duplicate" => [r.Conversions[0], r.Conversions[0]], _ => [r.Conversions[0] with { Bid = 2 }]
        } };
        Assert.False(Service("GBP").Evaluate(r).Passed);
    }

    [Fact]
    public void NonUsdAccountUsesConservativeAssetValuation()
    {
        var r = Request();
        r = r with { Portfolio = r.Portfolio with { NetLiquidation = new(2000, "EUR"), AvailableBuyingPower = new(2000, "EUR") },
            Conversions = [new("EUR", 1.1m, 1.10002m, Now, Now)] };
        Assert.Equal(1000m, Service().Evaluate(r).BaseUnits);
    }

    [Theory]
    [InlineData("gross")]
    [InlineData("net")]
    [InlineData("instrument")]
    [InlineData("strategy")]
    [InlineData("correlation")]
    [InlineData("currency")]
    public void ExistingStockAndFxPendingExposureReduceCapacity(string limit)
    {
        var r = Request();
        var held = new ForexExposure("SPY", "other", new(21000, "USD"), [new(21000, "USD")]);
        switch (limit)
        {
            case "gross": _risk.MaximumGrossExposure = 22001; break;
            case "net": _risk.MaximumNetExposure = 22001; break;
            case "instrument": _risk.MaximumInstrumentExposure = 22001; held = held with { InstrumentId = "EURUSD" }; break;
            case "strategy": _risk.MaximumStrategyExposure = 22001; held = held with { StrategyId = "patterns" }; break;
            case "correlation": _risk.CorrelationGroups["test"] = ["SPY", "EURUSD"]; _risk.MaximumCorrelationGroupExposure = 22001; break;
            case "currency": _profile.MaximumCurrencyExposureUsd = 22001; held = held with { CurrencyLegs = [new(19090.909090909090909m, "EUR")] }; break;
        }
        r = r with { Portfolio = r.Portfolio with { Exposures = [held], OpenPositionAndOrderCount = 1 },
            Conversions = [new("EUR", 1.1m, 1.1m, Now, Now)] };
        Assert.False(Service().Evaluate(r).Passed);
        Assert.Equal(0, Service().Evaluate(r).BaseUnits);
    }

    [Fact]
    public void LossAndUnreconciledStateBlockAssessment()
    {
        var r = Request();
        Assert.False(Service().Evaluate(r with { Portfolio = r.Portfolio with { Reconciled = false } }).Passed);
        Assert.False(Service().Evaluate(r with { Portfolio = r.Portfolio with { DailyRealizedPnl = new(-1000, "USD") } }).Passed);
        Assert.False(Service().Evaluate(r with { Portfolio = r.Portfolio with { OpenPositionAndOrderCount = 10 } }).Passed);
    }

    [Fact]
    public void PercentageFeesAndAdditionalOrderMinimumsAreIncluded()
    {
        _profile.CommissionPerOrderBps = 10m;
        var result = Service().Evaluate(Request());
        Assert.Equal(44.0404m, result.CommissionUsd);
        Assert.False(result.Passed); // high costs consume forecast edge
        _profile.CommissionPerOrderBps = .2m;
        _profile.ExitCommissionOrders = 2;
        Assert.Equal(6m, Service().Evaluate(Request()).CommissionUsd);
    }

    [Fact]
    public void DeterminismSnapshotsAndVersionHashProtectResearchReproducibility()
    {
        var svc = Service();
        var r = Request();
        var a = svc.Evaluate(r);
        Assert.Equal(a.InputHash, svc.Evaluate(r).InputHash);
        _profile.MinimumCommissionPerOrderUsd = 3;
        _risk.MaximumRiskPerTrade = 1;
        Assert.Equal(a.InputHash, svc.Evaluate(r).InputHash);
        Assert.NotEqual(a.InputHash, Service().Evaluate(r).InputHash);
        Assert.NotEqual(a.InputHash, svc.Evaluate(r with { ExpectedMidpointMove = .002m }).InputHash);
    }

    [Fact]
    public void NoFxProfileOrWrongStrategyAndStockContractFailClosed()
    {
        Assert.False(Service().Evaluate(Request() with { StrategyId = "other" }).Passed);
        var r = Request();
        Assert.False(Service().Evaluate(r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { Contract = new("SPY", "STK", "SMART", "USD") } } }).Passed);
        _profile.PipSize = -1;
        Assert.False(Service().Evaluate(r).Passed);
    }

    [Fact]
    public void InverseConversionSwapsBidAskAndPreservesTimestamps()
    {
        var conversion = UsdConversionQuote.FromPair("USD", "JPY", 150m, 151m, Now, Now);
        Assert.Equal(1m / 151m, conversion.Bid);
        Assert.Equal(1m / 150m, conversion.Ask);
        Assert.Equal("JPY", conversion.Currency);
        Assert.Equal(Now, conversion.EventUtc);
        Assert.Throws<ArgumentException>(() => UsdConversionQuote.FromPair("EUR", "GBP", .8m, .9m, Now, Now));
    }

    [Fact]
    public void RuleForDifferentVenueCannotBeUsedEvenWhenListed()
    {
        var r = Request();
        r = r with { Contract = r.Contract with { Metadata = r.Contract.Metadata! with { ValidExchanges = "OTHER,IDEALPRO", MarketRuleIds = "42,43" } } };
        Assert.False(Service().Evaluate(r).Passed);
        Assert.True(Service().Evaluate(r with { Rules = r.Rules! with { MarketRuleId = 43 } }).Passed);
    }

    [Fact]
    public void SlippageSafetyAndMultipleOrderFeesCannotDisappearFromRisk()
    {
        _profile.SlippagePerSidePips = 1;
        _profile.SafetyBufferBps = 1;
        var result = Service().Evaluate(Request());
        Assert.Equal(4m, result.SlippageUsd);
        Assert.Equal(10.60004m, result.TotalCostUsd);
        Assert.Equal(30.60004m, result.StopRiskUsd);
        _profile.CommissionPerOrderBps = 10;
        _profile.ExitCommissionOrders = 2;
        Assert.Equal(66.0804m, Service().Evaluate(Request()).CommissionUsd);
    }

    [Fact]
    public void FractionalQuantityStepAndBrokerMaximumAreRespected()
    {
        var r = Request();
        r = r with { MaximumBaseUnits = 20000.037m,
            Contract = r.Contract with { Metadata = r.Contract.Metadata! with { MinimumSize = .01m, SizeIncrement = .01m } },
            Rules = r.Rules! with { MinimumBaseUnits = .01m, BaseUnitIncrement = .01m } };
        Assert.Equal(20000.03m, Service().Evaluate(r).BaseUnits);
        Assert.Equal(15000.02m, Service().Evaluate(r with { Rules = r.Rules with { MaximumBaseUnits = 15000.029m } }).BaseUnits);
    }

    [Fact]
    public void PerInstrumentValueLimitCannotBeOverriddenByGlobalLimit()
    {
        var trading = Trading();
        trading.Instruments[0].MaximumPositionValue = 1200;
        var service = new ForexRiskService(Options.Create(new ForexRiskSettings { Profiles = [_profile] }), Options.Create(_risk), Options.Create(trading));
        Assert.Equal(1000m, service.Evaluate(Request()).BaseUnits);
    }

    [Fact]
    public void InvalidStopsAndExhaustedRiskCannotProduceSize()
    {
        Assert.False(Service().Evaluate(Request() with { StopPrice = 1.2m }).Passed);
        Assert.False(Service().Evaluate(Request() with { TargetPrice = 1m }).Passed);
        _risk.MaximumRiskPerTrade = 3m; // less than the round-trip minimum commission
        Assert.Equal(0, Service().Evaluate(Request()).BaseUnits);
    }
}
