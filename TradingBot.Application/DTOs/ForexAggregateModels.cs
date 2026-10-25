using TradingBot.Domain.Models;

namespace TradingBot.Application.DTOs;

/// <summary>Observed midpoint OHLC, not traded prices or executable OHLC. No carried-forward empty bars.</summary>
public sealed record ForexQuoteBar
{
    public string Version { get; init; } = string.Empty;
    public int IntervalSeconds { get; init; }
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndUtc { get; init; }
    public DateTime AvailableAtUtc { get; init; }
    public DateTime LastQuoteTimeUtc { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public decimal LastBid { get; init; }
    public decimal LastAsk { get; init; }
    public decimal MeanSpread { get; init; }
    public decimal MaximumSpread { get; init; }
    public int QuoteCount { get; init; }
    public decimal? TradeVolume => null;
    public int EmptyIntervalsBefore { get; init; }
}

public sealed record ForexAggregateDiagnostics(long RejectedQuoteEvents, long LateQuotes, long IncompleteQuotes);

public sealed record ForexFeatureReplayResult(string InputSha256, string AggregateVersion,
    IReadOnlyList<MarketFeatures> Frames, ForexAggregateDiagnostics Diagnostics);
