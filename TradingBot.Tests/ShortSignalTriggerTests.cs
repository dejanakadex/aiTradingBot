using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Tests;

public sealed class ShortSignalTriggerTests
{
    private static readonly DateTime Start = new(2026, 9, 24, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task CompletedBarsTriggerWithinMinuteAcrossInstrumentsAndStrategiesWithoutDuplicates()
    {
        var trading = new TradingSettings
        {
            Instruments =
            [
                new InstrumentSettings { InstrumentId = "SPY-ID", Symbol = "SPY", StrategyIds = ["alpha", "beta"] },
                new InstrumentSettings { InstrumentId = "QQQ-ID", Symbol = "QQQ", StrategyIds = ["alpha"] }
            ]
        };
        var profiles = new ShortSignalSettings
        {
            Profiles =
            [
                Profile("SPY-ID", "alpha"), Profile("SPY-ID", "beta"), Profile("QQQ-ID", "alpha")
            ]
        };
        var latest = new LatestMarketDataService();
        foreach (var instrument in trading.GetEnabledInstruments())
            for (var index = 0; index < 6; index++)
                latest.Apply(Trade(instrument, index, 100m + index));
        latest.AdvanceTo(Start.AddSeconds(32));

        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        var trigger = new ShortSignalTriggerService(
            Options.Create(profiles), Options.Create(trading), Options.Create(new ShortAggregateSettings()),
            latest, new AlwaysBullishDetectorFactory(), new FeatureEngine(), bus,
            NullLogger<ShortSignalTriggerService>.Instance);
        await Task.WhenAll(trading.GetEnabledInstruments().Select(async instrument =>
        {
            await trigger.ProcessAsync(instrument, Start.AddSeconds(32));
            await trigger.ProcessAsync(instrument, Start.AddSeconds(32));
        }));

        var emitted = new List<PatternCandidate>();
        while (bus.PatternCandidateReader.TryRead(out var candidate)) emitted.Add(candidate);
        Assert.Equal(3, emitted.Count);
        Assert.Equal(3, emitted.Select(candidate => candidate.PatternKey).Distinct().Count());
        Assert.Equal(3, emitted.Select(candidate => candidate.Context.SignalId).Distinct().Count());
        Assert.All(emitted, candidate =>
        {
            Assert.Equal(Timeframe.FiveSeconds, candidate.Timeframe);
            Assert.True(candidate.DetectedAtUtc < Start.AddMinutes(1));
            Assert.Equal(Start.AddSeconds(20), candidate.DetectedAtUtc);
        });
        Assert.Contains(emitted, candidate => candidate.InstrumentId == "SPY-ID" && candidate.StrategyId == "alpha");
        Assert.Contains(emitted, candidate => candidate.InstrumentId == "SPY-ID" && candidate.StrategyId == "beta");
        Assert.Contains(emitted, candidate => candidate.InstrumentId == "QQQ-ID" && candidate.StrategyId == "alpha");
    }

    [Fact]
    public async Task AsOfAndMissingWindowsDoNotLeakFutureOrBuildFalsePatternHistory()
    {
        var trading = new TradingSettings { Instruments = [new InstrumentSettings { InstrumentId = "SPY-ID", Symbol = "SPY", StrategyIds = ["alpha"] }] };
        var instrument = Assert.Single(trading.GetEnabledInstruments());
        var latest = new LatestMarketDataService();
        latest.Apply(Trade(instrument, 0, 100m));
        latest.Apply(Trade(instrument, 1, 101m));
        latest.Apply(Trade(instrument, 4, 104m)); // Two empty 5s windows separate the next bar.
        latest.AdvanceTo(Start.AddSeconds(27));
        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        var profile = Profile("SPY-ID", "alpha");
        profile.CooldownSeconds = 0;
        profile.MaximumSignalAgeSeconds = 30;
        var trigger = new ShortSignalTriggerService(
            Options.Create(new ShortSignalSettings { Profiles = [profile] }),
            Options.Create(trading), Options.Create(new ShortAggregateSettings()), latest,
            new AlwaysBullishDetectorFactory(), new FeatureEngine(), bus,
            NullLogger<ShortSignalTriggerService>.Instance);

        await trigger.ProcessAsync(instrument, Start.AddSeconds(7));
        Assert.False(bus.PatternCandidateReader.TryRead(out _)); // Only one bar is available at this as-of.
        await trigger.ProcessAsync(instrument, Start.AddSeconds(12));
        Assert.True(bus.PatternCandidateReader.TryRead(out var first));
        Assert.Equal(Start.AddSeconds(10), first!.DetectedAtUtc);
        await trigger.ProcessAsync(instrument, Start.AddSeconds(27));
        Assert.False(bus.PatternCandidateReader.TryRead(out _)); // The 20–25s bar has no contiguous predecessor.
    }

    [Fact]
    public async Task RealDetectorEmitsConfiguredEngulfingFromTradeTicksBeforeMinuteClose()
    {
        var trading = new TradingSettings { Instruments = [new InstrumentSettings { InstrumentId = "SPY-ID", Symbol = "SPY", StrategyIds = ["micro-engulfing"] }] };
        var instrument = Assert.Single(trading.GetEnabledInstruments());
        var latest = new LatestMarketDataService();
        var shapes = new (decimal Open, decimal High, decimal Low, decimal Close, decimal Size)[]
        {
            (16m, 16.2m, 15.8m, 16m, 25m),
            (15.8m, 16m, 15.6m, 15.8m, 25m),
            (15.6m, 15.8m, 15.4m, 15.6m, 25m),
            (15.4m, 15.6m, 15.1m, 15.4m, 25m),
            (15m, 16m, 14m, 14.5m, 25m),
            (14.4m, 16m, 14.4m, 16m, 75m)
        };
        for (var index = 0; index < shapes.Length; index++)
        {
            var shape = shapes[index];
            var prices = new[] { shape.Open, shape.High, shape.Low, shape.Close };
            for (var tick = 0; tick < prices.Length; tick++)
            {
                var eventTime = Start.AddSeconds(index * 5 + tick + 0.25);
                latest.Apply(new CanonicalMarketDataEvent
                {
                    EventId = $"{index}-{tick}", InstrumentId = instrument.InstrumentId,
                    Symbol = instrument.Symbol, Kind = MarketDataEventKind.Trade,
                    EventTimeUtc = eventTime, ReceivedTimeUtc = eventTime.AddMilliseconds(100),
                    Source = "Test.Tick", Price = prices[tick], Size = shape.Size
                });
            }
        }
        latest.AdvanceTo(Start.AddSeconds(32));
        using var bus = new TradingEventBus(new TradingEventBusOptions(), NullLogger<TradingEventBus>.Instance);
        var profile = Profile("SPY-ID", "micro-engulfing");
        profile.MinimumBars = 6;
        var trigger = new ShortSignalTriggerService(
            Options.Create(new ShortSignalSettings { Profiles = [profile] }),
            Options.Create(trading), Options.Create(new ShortAggregateSettings()), latest,
            new PatternDetectorFactory(Options.Create(new PatternDetectorOptions()), NullLoggerFactory.Instance),
            new FeatureEngine(), bus, NullLogger<ShortSignalTriggerService>.Instance);

        await trigger.ProcessAsync(instrument, Start.AddSeconds(32));

        Assert.True(bus.PatternCandidateReader.TryRead(out var candidate));
        Assert.Equal(PatternType.BullishEngulfing, candidate!.PatternType);
        Assert.Equal(Start.AddSeconds(30), candidate.DetectedAtUtc);
        Assert.Equal(Timeframe.FiveSeconds, candidate.Timeframe);
        Assert.Contains("+short-5s-v1", candidate.Context.FeatureVersion, StringComparison.Ordinal);
        Assert.False(bus.PatternCandidateReader.TryRead(out _));
    }

    [Fact]
    public void InvalidOrUnconfiguredProfilesFailClosed()
    {
        var trading = new TradingSettings { Instruments = [new InstrumentSettings { InstrumentId = "SPY-ID", Symbol = "SPY", StrategyIds = ["alpha"] }] };
        var profile = Profile("SPY-ID", "unknown");
        profile.IntervalSeconds = 1;
        var settings = new ShortSignalSettings { Profiles = [profile] };
        var errors = settings.GetValidationErrors(trading, new ShortAggregateSettings());
        Assert.Contains(errors, error => error.Contains("not enabled", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("not an enabled", StringComparison.Ordinal));
    }

    private static ShortSignalProfile Profile(string instrumentId, string strategyId) => new()
    {
        InstrumentId = instrumentId, StrategyId = strategyId, IntervalSeconds = 5,
        MinimumBars = 2, MaximumSignalAgeSeconds = 15, CooldownSeconds = 30,
        MinimumConfidence = 0.6m, PatternTypes = [PatternType.BullishEngulfing]
    };

    private static CanonicalMarketDataEvent Trade(ConfiguredInstrument instrument, int index, decimal price) => new()
    {
        EventId = $"{instrument.InstrumentId}-{index}", InstrumentId = instrument.InstrumentId,
        Symbol = instrument.Symbol, Kind = MarketDataEventKind.Trade,
        EventTimeUtc = Start.AddSeconds(index * 5 + 1),
        ReceivedTimeUtc = Start.AddSeconds(index * 5 + 1.1),
        Source = "Test.Tick", Price = price, Size = 1m
    };

    private sealed class AlwaysBullishDetectorFactory : IPatternDetectorFactory
    {
        public string DetectorVersion => "test-pattern-v1";
        public IPatternDetector Create() => new AlwaysBullishDetector();
    }

    private sealed class AlwaysBullishDetector : IPatternDetector
    {
        public IReadOnlyList<PatternCandidate> Detect(IReadOnlyList<Candle> candles) => [];

        public PatternDetectionBatch Process(PatternDetectionInput input)
        {
            var bar = input.Candles[^1];
            var pattern = new PatternCandidate(PatternType.BullishEngulfing, bar.Symbol, bar.Timeframe,
                bar.TimestampUtc, 0.8m,
                context: PipelineContext.CreateForSignal(bar.InstrumentId,
                    $"long|engulfing|{bar.Timeframe}|{bar.TimestampUtc:O}", input.StrategyId, input.FeatureVersion, "test-pattern-v1"));
            return new PatternDetectionBatch
            {
                Candidates = [pattern],
                Evaluations = [PatternEvaluation.FromCandidate(pattern)]
            };
        }
    }
}
