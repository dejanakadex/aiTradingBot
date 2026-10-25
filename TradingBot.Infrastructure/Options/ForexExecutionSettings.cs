namespace TradingBot.Infrastructure.Options;

public sealed class ForexExecutionSettings
{
    public ForexExecutionProfile[] Profiles { get; set; } = [];
    public void NormalizeBoundTimestamps()
    {
        // ConfigurationBinder parses an ISO offset/Z timestamp as Local. Preserve its instant;
        // offset-free Unspecified values still fail Validate().
        foreach (var profile in Profiles ?? [])
            if (profile != null && profile.ReviewedAtUtc.Kind == DateTimeKind.Local)
                profile.ReviewedAtUtc = profile.ReviewedAtUtc.ToUniversalTime();
    }
    public void Validate()
    {
        if (Profiles == null || Profiles.Any(x => x == null || string.IsNullOrWhiteSpace(x.InstrumentId)
            || x.MinimumBaseUnits <= 0 || x.MaximumBaseUnits < x.MinimumBaseUnits
            || x.ReviewedAtUtc.Kind != DateTimeKind.Utc || string.IsNullOrWhiteSpace(x.EvidenceSource)
            || x.TrailingIntervalSeconds is not (1 or 5 or 15))
            || Profiles.Select(x => x.InstrumentId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Length)
            throw new ArgumentException("FX execution profiles require unique instruments, reviewed size limits, UTC evidence and a supported short interval.");
    }
}

/// <summary>Explicitly reviewed venue size limits. No inferred odd-lot permission or precision override.</summary>
public sealed class ForexExecutionProfile
{
    public string InstrumentId { get; set; } = "";
    public decimal MinimumBaseUnits { get; set; }
    public decimal MaximumBaseUnits { get; set; }
    public DateTime ReviewedAtUtc { get; set; }
    public string EvidenceSource { get; set; } = "";
    public int TrailingIntervalSeconds { get; set; } = 5;
}
