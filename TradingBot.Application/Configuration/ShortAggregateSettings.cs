namespace TradingBot.Application.Configuration;

public sealed class ShortAggregateSettings
{
    public int[] IntervalsSeconds { get; set; } = [5, 15];
    public int AllowedLatenessSeconds { get; set; } = 2;
    public int MaxCompletedPerInterval { get; set; } = 512;
    public int ForexMaximumQuoteAgeSeconds { get; set; } = 2;

    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (IntervalsSeconds is null || IntervalsSeconds.Length == 0
            || IntervalsSeconds.Any(x => x is not (1 or 5 or 15))
            || IntervalsSeconds.Distinct().Count() != IntervalsSeconds.Length)
            errors.Add("ShortAggregates:IntervalsSeconds must be a unique nonempty subset of 1, 5 and 15.");
        if (AllowedLatenessSeconds < 0 || AllowedLatenessSeconds > 15)
            errors.Add("ShortAggregates:AllowedLatenessSeconds must be between 0 and 15.");
        if (MaxCompletedPerInterval < 2)
            errors.Add("ShortAggregates:MaxCompletedPerInterval must be at least 2.");
        if (ForexMaximumQuoteAgeSeconds < 0 || ForexMaximumQuoteAgeSeconds > 30)
            errors.Add("ShortAggregates:ForexMaximumQuoteAgeSeconds must be between 0 and 30.");
        return errors;
    }
}
