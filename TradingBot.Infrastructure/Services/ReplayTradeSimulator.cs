using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

/// <summary>Event-time, long-only fixed-bracket broker model. No live service dependency.</summary>
public sealed class ReplayTradeSimulator
{
    private readonly IPositionSizer _sizer;
    private readonly ScalpingExecutionSettings _execution;

    public ReplayTradeSimulator(IPositionSizer sizer, IOptions<RiskSettings> risk,
        IOptions<ScalpingExecutionSettings> execution)
    {
        _sizer = sizer;
        _execution = execution.Value;
        var settingsJson = JsonSerializer.Serialize(new { Sizer = sizer.GetType().FullName,
            Risk = risk.Value, Execution = _execution });
        ConfigurationFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settingsJson))).ToLowerInvariant();
    }

    public string ConfigurationFingerprint { get; }

    public IReadOnlyList<ReplayTradeResult> Simulate(
        IReadOnlyList<MarketDatasetRecord> events,
        IReadOnlyList<ReplaySignalSnapshot> signals,
        ReplayBacktestRequest request)
    {
        Validate(request);
        var results = new List<ReplayTradeResult>(signals.Count);
        var equity = request.StartingEquity;
        var occupiedUntil = DateTime.MinValue;
        var sourceIndexes = events.Select((market, index) => (market.EventId, index))
            .ToDictionary(x => x.EventId, x => x.index, StringComparer.Ordinal);
        var orderedSignals = signals.Select(signal => (Signal: signal,
                Source: sourceIndexes.TryGetValue(signal.SourceEventId, out var index) ? index : -1))
            .OrderBy(x => x.Source).ThenBy(x => x.Signal.SignalId);
        var quoteCursor = -1;
        decimal? bid = null, ask = null;
        DateTime? bidAt = null, askAt = null;
        foreach (var item in orderedSignals)
        {
            var signal = item.Signal;
            var source = item.Source;
            if (source < 0) throw new InvalidDataException($"Replay signal {signal.SignalId} has no source event.");
            while (quoteCursor < source)
            {
                var market = events[++quoteCursor];
                if (!Healthy(market)) continue;
                if (market.DataType.Equals("bid", StringComparison.OrdinalIgnoreCase)) { bid = market.Price; bidAt = market.EventTimeUtc; }
                if (market.DataType.Equals("ask", StringComparison.OrdinalIgnoreCase)) { ask = market.Price; askAt = market.EventTimeUtc; }
            }
            if (signal.Direction != TradeDirection.Long)
            {
                results.Add(Empty(signal, "UnsupportedDirection"));
                continue;
            }
            if (signal.DetectedAtUtc < occupiedUntil)
            {
                results.Add(Empty(signal, "OverlappingPosition"));
                continue;
            }
            if (bid is not > 0m || ask is not > 0m || ask < bid || bidAt is null || askAt is null
                || signal.DetectedAtUtc - bidAt > TimeSpan.FromMilliseconds(_execution.MaximumQuoteAgeMilliseconds)
                || signal.DetectedAtUtc - askAt > TimeSpan.FromMilliseconds(_execution.MaximumQuoteAgeMilliseconds)
                || (bidAt.Value - askAt.Value).Duration() > TimeSpan.FromMilliseconds(_execution.MaximumBidAskSkewMilliseconds)
                || (ask.Value - bid.Value) / ((ask.Value + bid.Value) / 2m) * 10_000m > _execution.MaximumSpreadBps)
            {
                results.Add(Empty(signal, "NoExecutableQuote"));
                continue;
            }

            var stop = ask.Value * (1m - request.StopBps / 10_000m);
            var target = ask.Value * (1m + request.TargetBps / 10_000m);
            var sizing = _sizer.CalculatePositionSize(ask.Value, stop,
                new AccountInfo { NetLiquidation = equity, BuyingPower = equity, AvailableFunds = equity }, []);
            var requested = decimal.Floor(sizing.Quantity);
            if (requested < 1m)
            {
                results.Add(Empty(signal, "RiskSizeRejected"));
                continue;
            }
            var entryLimit = _execution.EntryPolicy switch
            {
                ScalpingOrderPolicy.PassiveLimit => bid.Value,
                ScalpingOrderPolicy.MarketableLimit => ask.Value * (1m + _execution.MarketableLimitOffsetBps / 10_000m),
                _ => decimal.MaxValue
            };
            var expectedEntry = _execution.EntryPolicy == ScalpingOrderPolicy.Market ? ask.Value : entryLimit;
            var expectedCommission = 2m * Math.Max(request.MinimumCommissionPerOrder,
                requested * request.CommissionPerShare) / (requested * expectedEntry) * 10_000m;
            var expectedNetEdge = (target - expectedEntry) / expectedEntry * 10_000m
                - expectedCommission - 2m * request.SlippageBps - _execution.SafetyBufferBps;
            if (expectedNetEdge < _execution.MinimumNetEdgeBps)
            {
                results.Add(Empty(signal, "InsufficientNetEdge", requested));
                continue;
            }
            var entryReady = signal.DetectedAtUtc.AddMilliseconds(request.OrderLatencyMilliseconds);
            var entryExpires = signal.DetectedAtUtc.AddSeconds(request.EntryTimeoutSeconds);
            var filled = 0m;
            var entryValue = 0m;
            var exited = 0m;
            var exitValue = 0m;
            DateTime? firstFill = null, lastExit = null;
            var reason = string.Empty;
            decimal? horizon = null;
            for (var i = source + 1; i < events.Count; i++)
            {
                var market = events[i];
                if (!Healthy(market)) continue;
                var time = market.EventTimeUtc;
                var kind = market.DataType.ToLowerInvariant();
                if (firstFill.HasValue && !horizon.HasValue && kind == "bid"
                    && time >= firstFill.Value.AddSeconds(request.HoldingSeconds))
                    horizon = (market.Price!.Value - entryValue / filled) / (entryValue / filled) * 10_000m;

                if (exited == 0m && filled < requested && time >= entryReady && time <= entryExpires)
                {
                    var eligible = _execution.EntryPolicy == ScalpingOrderPolicy.PassiveLimit
                        ? kind == "trade" && market.Price < entryLimit
                        : kind == "ask" && (_execution.EntryPolicy == ScalpingOrderPolicy.Market || market.Price < entryLimit);
                    if (eligible && market.Size is > 0m)
                    {
                        var quantity = Math.Min(requested - filled, decimal.Floor(market.Size.Value * request.MaximumParticipation));
                        if (quantity > 0m)
                        {
                            var basePrice = _execution.EntryPolicy == ScalpingOrderPolicy.PassiveLimit ? market.Price!.Value : market.Price!.Value;
                            var price = basePrice * (1m + request.SlippageBps / 10_000m);
                            if (_execution.EntryPolicy == ScalpingOrderPolicy.Market || price <= entryLimit)
                            {
                                filled += quantity;
                                entryValue += quantity * price;
                                firstFill ??= time;
                            }
                        }
                    }
                }

                if (firstFill.HasValue && kind == "bid" && time > firstFill.Value && exited < filled
                    && market.Size is > 0m)
                {
                    var deadline = firstFill.Value.AddSeconds(request.HoldingSeconds);
                    var exitReason = reason switch
                    {
                        "Stop" or "Timer" or "TargetThenStop" or "TargetThenTimer" => reason,
                        "Target" when market.Price <= stop => "TargetThenStop",
                        "Target" when time >= deadline => "TargetThenTimer",
                        "Target" when market.Price > target => "Target",
                        "Target" => string.Empty,
                        _ when market.Price <= stop => "Stop",
                        _ when market.Price > target => "Target",
                        _ when time >= deadline => "Timer",
                        _ => string.Empty
                    };
                    if (exitReason.Length > 0)
                    {
                        var quantity = Math.Min(filled - exited, decimal.Floor(market.Size.Value * request.MaximumParticipation));
                        if (quantity > 0m)
                        {
                            var price = market.Price!.Value * (1m - request.SlippageBps / 10_000m);
                            exited += quantity;
                            exitValue += quantity * price;
                            lastExit = time;
                            reason = exitReason;
                        }
                    }
                }
                if (filled > 0m && exited == filled && reason.Length > 0 && horizon.HasValue) break;
                if (filled == 0m && time > entryExpires) break;
            }
            if (filled == 0m)
            {
                results.Add(Empty(signal,
                    events[^1].EventTimeUtc < entryExpires ? "EntryPendingAtDatasetEnd" : "Unfilled", requested));
                occupiedUntil = entryExpires;
                continue;
            }
            var closed = exited == filled;
            var entryAverage = entryValue / filled;
            var exitAverage = exited > 0m ? exitValue / exited : (decimal?)null;
            var gross = closed ? exitValue - entryValue : (decimal?)null;
            var commission = closed
                ? Math.Max(request.MinimumCommissionPerOrder, filled * request.CommissionPerShare)
                  + Math.Max(request.MinimumCommissionPerOrder, exited * request.CommissionPerShare)
                : (decimal?)null;
            var net = gross - commission;
            if (net.HasValue) equity += net.Value;
            occupiedUntil = closed ? lastExit!.Value : DateTime.MaxValue;
            results.Add(new ReplayTradeResult(signal.SignalId, closed ? "Closed" : "OpenAtDatasetEnd",
                closed ? reason : "InsufficientExitLiquidityOrData", signal.DetectedAtUtc,
                firstFill, lastExit, requested, filled, exited, entryAverage, exitAverage,
                gross, commission, net, horizon));
        }
        return results;
    }

    private static bool Healthy(MarketDatasetRecord market) => market.IsFinal && market.CanTriggerTrading
        && market.Price is > 0m && market.QualityStatus.Equals("Healthy", StringComparison.OrdinalIgnoreCase);

    private static ReplayTradeResult Empty(ReplaySignalSnapshot signal, string reason, decimal requested = 0m) =>
        new(signal.SignalId, reason, reason, signal.DetectedAtUtc, null, null, requested, 0m, 0m,
            null, null, null, null, null, null);

    public static void Validate(ReplayBacktestRequest request)
    {
        if (request.HoldingSeconds <= 0 || request.EntryTimeoutSeconds <= 0 || request.OrderLatencyMilliseconds < 0
            || request.StopBps <= 0m || request.StopBps >= 10_000m || request.TargetBps <= 0m
            || request.StartingEquity <= 0m || request.CommissionPerShare < 0m
            || request.MinimumCommissionPerOrder < 0m || request.SlippageBps < 0m
            || request.MaximumParticipation is <= 0m or > 1m)
            throw new ArgumentOutOfRangeException(nameof(request), "Replay backtest parameters are invalid.");
    }
}
