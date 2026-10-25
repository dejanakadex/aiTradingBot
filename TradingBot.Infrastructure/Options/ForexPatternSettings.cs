using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Microsoft.Extensions.Configuration;

namespace TradingBot.Infrastructure.Options;

/// <summary>Opt-in research profiles. No stock defaults or execution approval are inherited.</summary>
public sealed class ForexPatternSettings
{
    public ForexPatternProfile[] Profiles { get; set; } = [];

    public void ReplaceConfiguredArrays(IConfiguration section)
    {
        var configured = section.GetSection("Profiles").GetChildren().ToArray();
        for (var i = 0; i < Math.Min(configured.Length, Profiles.Length); i++)
            Profiles[i].Geometry.AiAnalysisTimeframes = configured[i].GetSection("Geometry:AiAnalysisTimeframes").Get<string[]>()
                ?? Profiles[i].Geometry.AiAnalysisTimeframes;
    }

    public IReadOnlyList<string> Validate()
    {
        if (Profiles == null) return ["ForexPatterns:Profiles cannot be null."];
        var errors = Profiles.SelectMany(x => x?.Validate() ?? ["FX profile cannot be null."]).ToList();
        if (Profiles.Where(x => x != null).GroupBy(x => $"{x.InstrumentId}|{x.StrategyId}|{x.IntervalSeconds}", StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            errors.Add("FX profiles must be unique by instrument, strategy and interval.");
        return errors;
    }
}

public sealed class ForexPatternProfile
{
    public string InstrumentId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; } = 5;
    // Null means both directions for research only, not permission to execute a short.
    public TradeDirection[]? Directions { get; set; }
    public int MinimumConsecutiveCandles { get; set; } = 26;
    public bool RequirePriceFeatureWarmup { get; set; } = true;
    public decimal EngulfingSupportProximityPercent { get; set; } = 0.0003m;
    public PatternDetectorOptions Geometry { get; set; } = CreateGeometry();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(InstrumentId) || string.IsNullOrWhiteSpace(StrategyId)
            || InstrumentId != InstrumentId.Trim() || StrategyId != StrategyId.Trim()) errors.Add("FX profile requires trimmed instrument and strategy IDs.");
        if (IntervalSeconds is not (1 or 5 or 15)) errors.Add("FX pattern interval must be 1, 5 or 15 seconds.");
        if (MinimumConsecutiveCandles is < 2 or > 10000) errors.Add("FX minimum candle count must be 2..10000.");
        if (Directions is { Length: 0 } || Directions?.Any(x => !Enum.IsDefined(x)) == true) errors.Add("FX research directions must be valid and nonempty.");
        if (EngulfingSupportProximityPercent is <= 0m or > 1m) errors.Add("FX engulfing proximity must be in (0,1].");
        if (Geometry == null) { errors.Add("FX geometry is required."); return errors; }
        if (Geometry.MinimumTradeSetupQuality is < 0m or > 1m) errors.Add("FX quality must be in [0,1].");
        foreach (var type in new[] { PatternType.Hammer, PatternType.ShootingStar, PatternType.BullishEngulfing, PatternType.BearishEngulfing,
            PatternType.DoubleBottom, PatternType.DoubleTop, PatternType.BreakoutAndRetest, PatternType.BreakdownAndRetest, PatternType.VwapReclaim, PatternType.VwapReject })
        {
            var rule = Geometry.ForPattern(type);
            if (rule == null) { errors.Add($"FX {type} options required."); continue; }
            if (new[] { rule.MinimumQualityForTradeSetup, rule.MinimumContextScore, rule.MinimumConfirmationScore, rule.MinimumVolumeScore }.Any(x => x < 0m || x > 1m))
                errors.Add($"FX {type} score thresholds must be in [0,1].");
            bool valid = rule switch
            {
                HammerPatternOptions x => Unit(x.MaxBodyToRange) && Unit(x.MinLowerWickToRange) && Unit(x.MaxUpperWickToRange)
                    && Unit(x.MinClosePositionInRange) && Count(x.PullbackLookbackCandles) && Percent(x.MinPullbackPercent) && Percent(x.SupportProximityPercent),
                BullishEngulfingPatternOptions x => x.MinBodyRatio > 0m && x.MinFirstBodyToAtr >= 0m && x.MinSecondBodyToAtr >= 0m
                    && Count(x.PullbackLookbackCandles) && Percent(x.MinPullbackPercent),
                DoubleBottomPatternOptions x => Percent(x.MaxLowDiffPercent) && Percent(x.MinReboundPercent) && Unit(x.ConfirmationTolerancePercent)
                    && x.MinSeparation >= 2 && x.MaxSeparation >= x.MinSeparation && x.MaxSeparation <= 10000,
                BreakoutAndRetestPatternOptions x => Count(x.Lookback) && Percent(x.RetestTolerancePercent) && Percent(x.MinBreakoutPercent) && Percent(x.MinBouncePercent),
                VwapReclaimPatternOptions x => Count(x.PriorBelowVwapCandles) && x.MinDistanceFromVwapToAtr >= 0m && Unit(x.MinClosePositionInRange),
                _ => false
            };
            if (!valid) errors.Add($"FX {type} geometry thresholds are invalid.");
        }
        return errors;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Version => PipelineContractVersions.ForexPatterns + "+config-" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, HashOptions)))).ToLowerInvariant();

    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web);

    public ForexPatternProfile Snapshot() => JsonSerializer.Deserialize<ForexPatternProfile>(JsonSerializer.Serialize(this, HashOptions), HashOptions)!;
    private static bool Unit(decimal x) => x is >= 0m and <= 1m;
    private static bool Percent(decimal x) => x is > 0m and <= 1m;
    private static bool Count(int x) => x is >= 2 and <= 10000;

    private static PatternDetectorOptions CreateGeometry()
    {
        var options = new PatternDetectorOptions();
        foreach (var x in new[] { options.Hammer, options.ShootingStar })
        { x.MinPullbackPercent = 0.0001m; x.SupportProximityPercent = 0.0003m; }
        foreach (var x in new[] { options.BullishEngulfing, options.BearishEngulfing })
        { x.MinPullbackPercent = 0.0001m; x.RequireVolumeConfirmation = false; }
        foreach (var x in new[] { options.DoubleBottom, options.DoubleTop })
        { x.MaxLowDiffPercent = 0.0003m; x.MinReboundPercent = 0.0002m; x.ConfirmationTolerancePercent = 0.0001m; }
        foreach (var x in new[] { options.BreakoutAndRetest, options.BreakdownAndRetest })
        { x.RetestTolerancePercent = 0.0003m; x.MinBreakoutPercent = 0.0001m; x.MinBouncePercent = 0.0001m; x.RequireVolumeConfirmation = false; }
        return options;
    }
}
