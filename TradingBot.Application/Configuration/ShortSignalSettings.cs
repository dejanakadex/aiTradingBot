using TradingBot.Domain.Enums;

namespace TradingBot.Application.Configuration;

public sealed class ShortSignalSettings
{
    public ShortSignalProfile[] Profiles { get; set; } = [];

    public ShortSignalProfile? Find(string instrumentId, string strategyId, Timeframe timeframe) =>
        (Profiles ?? []).FirstOrDefault(profile => profile.Enabled
            && profile.InstrumentId.Equals(instrumentId, StringComparison.OrdinalIgnoreCase)
            && profile.StrategyId.Equals(strategyId, StringComparison.OrdinalIgnoreCase)
            && profile.Timeframe == timeframe);

    public IReadOnlyList<string> GetValidationErrors(TradingSettings? trading = null, ShortAggregateSettings? aggregates = null)
    {
        var errors = new List<string>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in Profiles ?? [])
        {
            if (!profile.Enabled) continue;
            if (string.IsNullOrWhiteSpace(profile.InstrumentId) || string.IsNullOrWhiteSpace(profile.StrategyId))
                errors.Add("ShortSignals profiles require InstrumentId and StrategyId.");
            if (profile.IntervalSeconds is not (1 or 5 or 15))
                errors.Add("ShortSignals interval must be 1, 5 or 15 seconds.");
            if (profile.MinimumBars is < 2 or > 80 || profile.CooldownSeconds < 0
                || profile.MaximumSignalAgeSeconds is < 1 or > 300
                || profile.MinimumConfidence is < 0m or > 1m)
                errors.Add("ShortSignals thresholds, cooldown, age or minimum bars are invalid.");
            if (profile.PatternTypes is null || profile.PatternTypes.Length == 0
                || profile.PatternTypes.Any(type => type is not (
                    PatternType.Hammer or PatternType.BullishEngulfing or PatternType.DoubleBottom
                    or PatternType.BreakoutAndRetest or PatternType.VwapReclaim
                    or PatternType.ShootingStar or PatternType.BearishEngulfing or PatternType.DoubleTop
                    or PatternType.BreakdownAndRetest or PatternType.VwapReject))
                || profile.PatternTypes.Distinct().Count() != profile.PatternTypes.Length)
                errors.Add("ShortSignals requires unique pattern types supported by the existing detector.");
            if (!keys.Add($"{profile.InstrumentId}|{profile.StrategyId}|{profile.IntervalSeconds}"))
                errors.Add("ShortSignals contains a duplicate instrument/strategy/interval profile.");
            if (aggregates != null && !(aggregates.IntervalsSeconds ?? []).Contains(profile.IntervalSeconds))
                errors.Add($"ShortSignals interval {profile.IntervalSeconds}s is not enabled in ShortAggregates.");
            if (trading != null && !trading.GetEnabledInstruments().Any(instrument =>
                    instrument.InstrumentId.Equals(profile.InstrumentId, StringComparison.OrdinalIgnoreCase)
                    && instrument.StrategyIds.Contains(profile.StrategyId, StringComparer.OrdinalIgnoreCase)))
                errors.Add($"ShortSignals profile {profile.InstrumentId}/{profile.StrategyId} is not an enabled configured instrument/strategy.");
            if (trading?.GetConfiguredInstruments().Any(x => x.InstrumentId.Equals(profile.InstrumentId, StringComparison.OrdinalIgnoreCase)
                && x.InstrumentType == InstrumentType.Forex) == true)
                errors.Add("FX signals require ForexSignals profiles; stock ShortSignals cannot route FX candidates.");
        }
        return errors;
    }
}

public sealed class ShortSignalProfile
{
    public bool Enabled { get; set; } = true;
    public string InstrumentId { get; set; } = string.Empty;
    public string StrategyId { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; } = 5;
    public PatternType[] PatternTypes { get; set; } = [PatternType.BullishEngulfing];
    public int MinimumBars { get; set; } = 6;
    public decimal MinimumConfidence { get; set; } = 0.6m;
    public int CooldownSeconds { get; set; } = 30;
    public int MaximumSignalAgeSeconds { get; set; } = 15;

    public Timeframe Timeframe => IntervalSeconds switch
    {
        1 => Timeframe.OneSecond,
        5 => Timeframe.FiveSeconds,
        15 => Timeframe.FifteenSeconds,
        _ => throw new InvalidOperationException("Invalid short signal interval.")
    };
}
