namespace TradingBot.Infrastructure.Options;

/// <summary>Explicit research profiles. No profile enables broker execution.</summary>
public sealed class ForexRiskSettings
{
    public ForexRiskProfile[] Profiles { get; set; } = [];

    public IReadOnlyList<string> Validate()
    {
        if (Profiles == null || Profiles.Any(x => x == null)) return ["FX risk profiles cannot be null."];
        var errors = new List<string>();
        if (Profiles.GroupBy(x => (x.InstrumentId, x.StrategyId)).Any(x => x.Count() > 1))
            errors.Add("Duplicate FX risk instrument/strategy profile.");
        foreach (var p in Profiles)
        {
            if (string.IsNullOrWhiteSpace(p.InstrumentId) || string.IsNullOrWhiteSpace(p.StrategyId)
                || p.InstrumentId != p.InstrumentId.Trim() || p.StrategyId != p.StrategyId.Trim())
                errors.Add("FX risk requires trimmed instrument and strategy IDs.");
            if (p.PipSize <= 0 || p.MaximumCurrencyExposureUsd <= 0 || p.MaximumSpreadBps <= 0
                || p.MaximumSnapshotAgeSeconds is < 1 or > 60 || p.MaximumRulesAgeHours is < 1 or > 168
                || p.CommissionPerOrderBps < 0 || p.MinimumCommissionPerOrderUsd < 0
                || p.SlippagePerSidePips < 0 || p.SafetyBufferBps < 0 || p.MinimumNetEdgeBps < 0
                || p.EntryCommissionOrders < 1 || p.ExitCommissionOrders < 1
                || string.IsNullOrWhiteSpace(p.CommissionScheduleSource))
                errors.Add("FX risk units, limits, fee source or cost assumptions are invalid.");
        }
        return errors;
    }
}

public sealed class ForexRiskProfile
{
    public string InstrumentId { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public decimal PipSize { get; set; } = 0.0001m;
    public decimal MaximumCurrencyExposureUsd { get; set; } = 1000m;
    public decimal MaximumSpreadBps { get; set; } = 2m;
    public int MaximumSnapshotAgeSeconds { get; set; } = 5;
    public int MaximumRulesAgeHours { get; set; } = 24;
    // Research estimates: tier I spot FX, not automatic currency conversion pricing.
    public decimal CommissionPerOrderBps { get; set; } = 0.20m;
    public decimal MinimumCommissionPerOrderUsd { get; set; } = 2m;
    public int EntryCommissionOrders { get; set; } = 1;
    public int ExitCommissionOrders { get; set; } = 1;
    public string CommissionScheduleSource { get; set; } = "https://www.interactivebrokers.com/en/pricing/commissions-spot-currencies.php";
    public decimal SlippagePerSidePips { get; set; } = 0.1m;
    public decimal SafetyBufferBps { get; set; } = 0.5m;
    public decimal MinimumNetEdgeBps { get; set; } = 1m;
}
