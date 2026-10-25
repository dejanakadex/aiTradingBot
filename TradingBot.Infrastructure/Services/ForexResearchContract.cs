using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

public sealed class ForexResearchContract
{
    public const string Labels = "labels-fx-execution-v1";
    public const string Model = "numerical-model-fx-v1";
    private readonly ForexResearchSettings _research;
    private readonly ForexRiskSettings _costs;
    private readonly RiskSettings _risk;
    private readonly TradingSettings _trading;
    private readonly ForexPatternSettings _patterns;
    private readonly ForexSignalSettings _signals;
    private readonly ShortAggregateSettings _aggregates;
    private readonly CanonicalFeatureSettings _features;
    public ForexResearchContract(IOptions<ForexResearchSettings> research, IOptions<ForexRiskSettings> costs,
        IOptions<RiskSettings> risk, IOptions<TradingSettings> trading, IOptions<ForexPatternSettings> patterns,
        IOptions<ForexSignalSettings> signals, IOptions<ShortAggregateSettings> aggregates, IOptions<CanonicalFeatureSettings> features)
    {
        _research = Copy(research.Value); _costs = Copy(costs.Value); _risk = Copy(risk.Value); _trading = Copy(trading.Value);
        _patterns = Copy(patterns.Value); _signals = Copy(signals.Value); _aggregates = Copy(aggregates.Value); _features = Copy(features.Value);
        if (_research.Profiles == null || _research.Profiles.Any(x => x == null)
            || _research.Profiles.GroupBy(x => (x.InstrumentId, x.StrategyId)).Any(x => x.Count() != 1))
            throw new ArgumentException("FX research profiles must be unique per instrument/strategy.");
        // ConfigurationBinder represents ISO timestamps with an offset as Local DateTime.
        // Recover the same UTC instant; an offset-free Unspecified timestamp still fails validation.
        foreach (var profile in _research.Profiles)
            if (profile.HistoricalRules != null)
                profile.HistoricalRules = profile.HistoricalRules.Select(x => x == null || x.Rules == null ? x! : x with
                {
                    ObservedUtc = x.ObservedUtc.Kind == DateTimeKind.Local ? x.ObservedUtc.ToUniversalTime() : x.ObservedUtc,
                    Rules = x.Rules with { VerifiedAtUtc = x.Rules.VerifiedAtUtc.Kind == DateTimeKind.Local ? x.Rules.VerifiedAtUtc.ToUniversalTime() : x.Rules.VerifiedAtUtc }
                }).ToArray();
    }
    public (ForexResearchProfile Profile, ForexRiskProfile Costs, string Feature, string Pattern, string Label) Get(string instrumentId, string strategyId)
    {
        var p = _research.Profiles.SingleOrDefault(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId)
            ?? throw new InvalidOperationException("No explicit FX research contract matches this instrument/strategy.");
        var instrument = _trading.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
        if (instrument.InstrumentType != InstrumentType.Forex || instrument.Currency != "USD" || p.IntervalSeconds is not (1 or 5 or 15)
            || !_aggregates.IntervalsSeconds.Contains(p.IntervalSeconds) || p.HoldingSeconds is < 1 or > 3600
            || p.EntryTimeoutSeconds is < 1 or > 60 || p.LatencyMilliseconds is < 0 or > 10000
            || p.MaximumQuoteGapSeconds is < 1 or > 60 || p.StopPips <= 0 || p.TargetPips <= 0 || p.MaximumBaseUnits <= 0
            || p.StartingEquityUsd <= 0 || p.FillBaseUnitsPerQuote <= 0 || string.IsNullOrWhiteSpace(p.FillAssumptionSource)
            || p.HistoricalRules is not { Length: > 0 } || p.HistoricalRules.Any(x => x == null || x.Rules == null || x.Contract == null
                || x.ObservedUtc.Kind != DateTimeKind.Utc || x.Rules.VerifiedAtUtc.Kind != DateTimeKind.Utc)
            || p.Exit == null || p.Exit.BreakEvenTriggerR <= 0 || p.Exit.TrailingActivationR <= 0
            || p.Exit.TrailingAtrMultiplier <= 0 || p.Exit.BreakEvenOffsetR < 0 || !Enum.IsDefined(p.Exit.Mode))
            throw new InvalidOperationException("Invalid FX research rules, liquidity scenario, timing, exit or USD contract.");
        var costs = _costs.Profiles.Single(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId);
        var pattern = _patterns.Profiles.Single(x => x.InstrumentId == instrumentId && x.StrategyId == strategyId && x.IntervalSeconds == p.IntervalSeconds);
        var signal = _signals.Profiles.Single(x => x.Selection.InstrumentId == instrumentId && x.Selection.StrategyId == strategyId && x.Selection.IntervalSeconds == p.IntervalSeconds);
        var feature = new FeatureEngine(_features);
        var featureVersion = $"{feature.ForexFeatureVersion}+basis-{ForexQuoteAggregator.CandleSource}.{ForexQuoteAggregator.VersionFor(_aggregates)}.{p.IntervalSeconds}s";
        var patternVersion = new ForexPatternDetector(pattern, feature, NullLogger<PatternDetector>.Instance).Version + "+" + signal.Version;
        var label = Labels + "+config-" + Hash(JsonSerializer.Serialize(new { p, costs, risk = _risk,
            instrument = new { instrument.InstrumentId, instrument.Symbol, instrument.BaseCurrency, instrument.Currency, instrument.Exchange,
                instrument.SecurityType, instrument.AllowedDirections, instrument.MaximumHoldingSeconds }, featureVersion, patternVersion }));
        return (Copy(p), Copy(costs), featureVersion, patternVersion, label);
    }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
}
