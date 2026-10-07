namespace TradingBot.Application.Configuration;

public sealed class ShadowPerformanceSettings
{
    public bool Enabled { get; set; }
    public int MinimumDecisionsPerStrategy { get; set; } = 100;
    public int MinimumObservationMinutes { get; set; } = 30;
    public int MaximumP95SignalProcessingMilliseconds { get; set; } = 1000;
    public int MaximumP95SignalAgeMilliseconds { get; set; } = 1500;
    public int MaximumP95QuoteAgeMilliseconds { get; set; } = 1500;
    public int MaximumPendingPatternQueueDepth { get; set; } = 192;
    public decimal MinimumAverageExpectedNetEdgeBps { get; set; } = 3m;
    public int MaximumRecentQualityIncidents { get; set; }

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MinimumDecisionsPerStrategy < 1) errors.Add("MinimumDecisionsPerStrategy must be positive.");
        if (MinimumObservationMinutes < 1) errors.Add("MinimumObservationMinutes must be positive.");
        if (MaximumP95SignalProcessingMilliseconds < 1) errors.Add("MaximumP95SignalProcessingMilliseconds must be positive.");
        if (MaximumP95SignalAgeMilliseconds < 1) errors.Add("MaximumP95SignalAgeMilliseconds must be positive.");
        if (MaximumP95QuoteAgeMilliseconds < 1) errors.Add("MaximumP95QuoteAgeMilliseconds must be positive.");
        if (MaximumPendingPatternQueueDepth < 0) errors.Add("MaximumPendingPatternQueueDepth cannot be negative.");
        if (MinimumAverageExpectedNetEdgeBps < 0m) errors.Add("MinimumAverageExpectedNetEdgeBps cannot be negative.");
        if (MaximumRecentQualityIncidents < 0) errors.Add("MaximumRecentQualityIncidents cannot be negative.");
        return errors;
    }
}
