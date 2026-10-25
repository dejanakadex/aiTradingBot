using Microsoft.Extensions.Logging;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

/// <summary>Deterministic research only. Shares stock geometry, never publishes trading signals.</summary>
public sealed class ForexPatternDetector
{
    private readonly ForexPatternProfile _profile;
    private readonly IFeatureEngine _features;
    private readonly ILogger<PatternDetector> _logger;

    public ForexPatternDetector(ForexPatternProfile profile, IFeatureEngine features, ILogger<PatternDetector> logger)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = profile.Validate();
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(profile));
        _profile = profile.Snapshot();
        _features = features;
        _logger = logger;
        Version = _profile.Version;
    }

    public string Version { get; }

    public PatternDetectionBatch Evaluate(CanonicalFeatureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.ForexMidpoint || input.AsOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("FX research requires explicit ForexMidpoint and a UTC as-of time.", nameof(input));
        var available = input.Candles.Where(x => x.IsFinal && x.QualityStatus == MarketDataQualityStatus.Healthy
                && x.TimestampUtc <= input.AsOfUtc && x.ReceivedTimeUtc <= input.AsOfUtc)
            .ToArray();
        if (available.Length == 0) return new PatternDetectionBatch();
        var timeframe = _profile.IntervalSeconds switch { 1 => Timeframe.OneSecond, 5 => Timeframe.FiveSeconds, _ => Timeframe.FifteenSeconds };
        if (available.Any(x => x.InstrumentId != _profile.InstrumentId || x.Symbol != available[0].Symbol || x.Timeframe != timeframe || x.Source != available[0].Source
            || !x.Source.StartsWith(ForexQuoteAggregator.CandleSource + ".", StringComparison.Ordinal) || x.HasTradeVolume
            || x.ReceivedTimeUtc < x.TimestampUtc || x.Low <= 0m || x.Open < x.Low || x.Open > x.High || x.Close < x.Low || x.Close > x.High))
            throw new ArgumentException("FX research requires one profile's homogeneous short quote-midpoint series with unavailable trade volume.", nameof(input));
        var candles = available.OrderBy(x => x.TimestampUtc).ThenBy(x => x.ReceivedTimeUtc).ThenBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.Open).ThenBy(x => x.High).ThenBy(x => x.Low).ThenBy(x => x.Close).ThenBy(x => x.Volume)
            .GroupBy(x => x.TimestampUtc).Select(x => x.Last()).ToArray();
        var first = candles.Length - 1;
        while (first > 0 && candles[first].TimestampUtc - candles[first - 1].TimestampUtc == TimeSpan.FromSeconds(_profile.IntervalSeconds)) first--;
        candles = candles[first..];
        var features = _features.ComputeFeatures(new CanonicalFeatureInput { Candles = candles, AsOfUtc = input.AsOfUtc, ForexMidpoint = true,
            Bid = input.Bid, Ask = input.Ask, BidTimeUtc = input.BidTimeUtc, AskTimeUtc = input.AskTimeUtc });
        var conditions = new[]
        {
            new PatternConditionResult("fxConsecutiveWarmup", candles.Length >= _profile.MinimumConsecutiveCandles,
                $"FX profile requires {_profile.MinimumConsecutiveCandles} consecutive available candles; actual {candles.Length}."),
            new PatternConditionResult("fxPriceFeatureWarmup", !_profile.RequirePriceFeatureWarmup || features.PriceFeaturesReady == true,
                "FX profile requires warmed-up price features.")
        };
        var evaluations = new PatternDetector(_profile, Version, conditions, _logger).Evaluate(new PatternDetectionInput
        {
            Candles = candles, StrategyId = _profile.StrategyId, FeatureVersion = features.FeatureVersion,
            Directions = _profile.Directions ?? [TradeDirection.Long, TradeDirection.Short]
        });
        return new PatternDetectionBatch { Evaluations = evaluations, Candidates = evaluations.Where(x => x.Candidate != null).Select(x => x.Candidate!).ToArray() };
    }
}
