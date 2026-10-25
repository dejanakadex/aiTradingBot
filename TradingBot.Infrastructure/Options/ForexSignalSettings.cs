using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Options;

public sealed class ForexSignalSettings
{
    public ForexSignalProfile[] Profiles { get; set; } = [];

    public IReadOnlyList<string> Validate(TradingSettings? trading = null, ShortAggregateSettings? aggregates = null,
        ForexPatternSettings? patterns = null)
    {
        if (Profiles == null || Profiles.Any(x => x?.Selection == null)) return ["ForexSignals requires non-null profiles and selections."];
        // Reuse stock selection threshold validation, but validate FX membership separately.
        var errors = new ShortSignalSettings { Profiles = Profiles.Select(x => x.Selection).ToArray() }
            .GetValidationErrors(aggregates: aggregates).ToList();
        foreach (var profile in Profiles.Where(x => x.Selection.Enabled))
        {
            var selection = profile.Selection;
            if (string.IsNullOrWhiteSpace(selection.InstrumentId) || string.IsNullOrWhiteSpace(selection.StrategyId)
                || selection.InstrumentId != selection.InstrumentId.Trim() || selection.StrategyId != selection.StrategyId.Trim())
                errors.Add("FX signal instrument/strategy IDs must be trimmed.");
            if (profile.MaximumSpreadBps is <= 0m or > 100m || profile.MaximumQuoteAgeSeconds is < 1 or > 60
                || profile.MaximumAtrToPriceRatio is <= 0m or > 1m)
                errors.Add("FX spread, quote age and ATR/price limits are invalid.");
            if (selection.PatternTypes?.Any(x => x is PatternType.VwapReclaim or PatternType.VwapReject) == true)
                errors.Add("FX signal selection cannot require unavailable VWAP patterns.");
            if (trading != null && !trading.GetEnabledInstruments().Any(x => x.InstrumentId == selection.InstrumentId
                && x.InstrumentType == InstrumentType.Forex && x.StrategyIds.Contains(selection.StrategyId)))
                errors.Add($"FX signal profile {selection.InstrumentId}/{selection.StrategyId} requires an enabled configured FX instrument/strategy.");
            var pattern = patterns?.Profiles.SingleOrDefault(x => x.InstrumentId == selection.InstrumentId
                && x.StrategyId == selection.StrategyId && x.IntervalSeconds == selection.IntervalSeconds);
            if (patterns != null && pattern == null) errors.Add("FX signal profile requires a matching ForexPatterns profile.");
            if (aggregates != null && Math.Max(selection.MinimumBars, pattern?.MinimumConsecutiveCandles ?? 0) > aggregates.MaxCompletedPerInterval)
                errors.Add("FX pattern/signal warmup exceeds aggregate retention.");
        }
        return errors;
    }
}

public sealed class ForexSignalProfile
{
    // No automatic enrollment; directions come from the configured instrument and pattern profile.
    public ShortSignalProfile Selection { get; set; } = new() { Enabled = false, MinimumBars = 26, PatternTypes = [] };
    public decimal MaximumSpreadBps { get; set; } = 2m;
    public int MaximumQuoteAgeSeconds { get; set; } = 10;
    public decimal MaximumAtrToPriceRatio { get; set; } = 0.005m;
    public string? ApprovedSignalVersion { get; set; }
    public string? ApprovedPatternVersion { get; set; }
    public string? ApprovedFeatureVersion { get; set; }

    [JsonIgnore]
    public string Version => "signals-fx-shadow-v1+config-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Selection, MaximumSpreadBps, MaximumQuoteAgeSeconds, MaximumAtrToPriceRatio })))).ToLowerInvariant();

    public ForexSignalProfile Snapshot() => JsonSerializer.Deserialize<ForexSignalProfile>(JsonSerializer.Serialize(this))!;
}
