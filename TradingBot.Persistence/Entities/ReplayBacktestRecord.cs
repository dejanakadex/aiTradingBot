namespace TradingBot.Persistence;

public sealed class ReplayBacktestRecord
{
    public Guid Id { get; set; }
    public Guid ReplayRunId { get; set; }
    public string InputSha256 { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
    public string OutputSha256 { get; set; } = string.Empty;
    public string ResultsJson { get; set; } = string.Empty;
    public int SignalCount { get; set; }
    public int FilledTradeCount { get; set; }
    public int ClosedTradeCount { get; set; }
    public decimal RealizedNetPnl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
