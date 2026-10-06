namespace TradingBot.Application.DTOs;

public sealed record ReplayBacktestRequest(
    int HoldingSeconds = 60,
    int EntryTimeoutSeconds = 10,
    int OrderLatencyMilliseconds = 250,
    decimal StopBps = 15m,
    decimal TargetBps = 50m,
    decimal StartingEquity = 10_000m,
    decimal CommissionPerShare = 0.005m,
    decimal MinimumCommissionPerOrder = 1m,
    decimal SlippageBps = 1m,
    decimal MaximumParticipation = 0.25m);

public sealed record ReplayTradeResult(
    Guid SignalId,
    string Status,
    string ExitReason,
    DateTime SignalTimeUtc,
    DateTime? EntryTimeUtc,
    DateTime? ExitTimeUtc,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal ExitedQuantity,
    decimal? EntryPrice,
    decimal? ExitPrice,
    decimal? GrossPnl,
    decimal? Commission,
    decimal? NetPnl,
    decimal? HorizonReturnBps);

public sealed record ReplayBacktestSnapshot(
    Guid Id,
    Guid ReplayRunId,
    string InputSha256,
    string RequestSha256,
    string OutputSha256,
    int SignalCount,
    int FilledTradeCount,
    int ClosedTradeCount,
    decimal RealizedNetPnl,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReplayTradeResult> Trades);
