using System.Text.Json;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

public sealed record ForexReplayQuote(string Id, DateTime EventUtc, DateTime AvailableUtc, decimal Bid, decimal Ask);
public sealed record ForexReplayAtr(DateTime AvailableUtc, decimal Atr);
public sealed record ForexSimulationResult(ReplayTradeResult Trade, DateTime LabelWindowEndUtc,
    decimal? GrossReturnBps, decimal? CostBps, decimal? NetReturnBps, decimal? MfeBps, decimal? MaeBps,
    string InputHash, string RiskInputJson, string RiskAssessmentJson);

/// <summary>Quote-driven historical scenario, never a claim of executable liquidity or an IBKR fill.</summary>
public sealed class ForexTradeSimulator(ForexRiskService risk)
{
    public static ForexReplayQuote[] ReadQuotes(IReadOnlyList<MarketDatasetRecord> records, string instrumentId, string symbol)
    {
        if (records.Count == 0) throw new InvalidDataException("No FX paired quote data.");
        if (records.Any(x => x.InstrumentId != instrumentId || x.Symbol != symbol || !x.IsFinal || !x.CanPersist
            || x.QualityStatus != "Healthy" || x.Source != ForexQuoteAggregator.QuoteSource || x.Price is not > 0m
            || x.DataType is not ("bid" or "ask") || !x.EventId.StartsWith("IBKR:FXQ:", StringComparison.Ordinal)
            || !x.EventId.EndsWith(":" + x.DataType, StringComparison.Ordinal) || x.EventTimeUtc.Kind != DateTimeKind.Utc
            || x.ReceivedTimeUtc.Kind != DateTimeKind.Utc || x.EventTimeUtc > x.ReceivedTimeUtc))
            throw new InvalidDataException("FX replay requires healthy, canonical paired quotes with UTC availability.");
        var unique = records.GroupBy(x => x.EventId, StringComparer.Ordinal).Select(g =>
        {
            if (g.Select(x => JsonSerializer.Serialize(x)).Distinct().Count() != 1) throw new InvalidDataException("Conflicting duplicate quote ID.");
            return g.First();
        }).ToArray();
        var result = unique.GroupBy(x => x.EventId[..^4], StringComparer.Ordinal).Select(g =>
        {
            var bid = g.SingleOrDefault(x => x.DataType == "bid"); var ask = g.SingleOrDefault(x => x.DataType == "ask");
            if (g.Count() != 2 || bid == null || ask == null || bid.EventTimeUtc != ask.EventTimeUtc
                || bid.ReceivedTimeUtc != ask.ReceivedTimeUtc || ask.Price < bid.Price)
                throw new InvalidDataException("Incomplete, mismatched or crossed FX quote pair.");
            return new ForexReplayQuote(g.Key, bid.EventTimeUtc, bid.ReceivedTimeUtc, bid.Price!.Value, ask.Price!.Value);
        }).OrderBy(x => x.AvailableUtc).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (result.Zip(result.Skip(1)).Any(x => x.Second.EventUtc < x.First.EventUtc))
            throw new InvalidDataException("Out-of-order FX event times require a separate coverage range.");
        return result;
    }

    public ForexSimulationResult Simulate(Guid signalId, TradeDirection direction, DateTime decisionUtc,
        IReadOnlyList<ForexReplayQuote> quotes, IReadOnlyList<ForexReplayAtr> atrFrames,
        ForexResearchProfile p, ForexRiskProfile costs, decimal equity, decimal dailyLoss = 0m)
    {
        var end = decisionUtc.AddSeconds(p.EntryTimeoutSeconds + p.HoldingSeconds + p.MaximumQuoteGapSeconds)
            .AddMilliseconds(p.LatencyMilliseconds * 2);
        var prior = quotes.LastOrDefault(x => x.AvailableUtc <= decisionUtc);
        var future = quotes.Where(x => x.AvailableUtc > decisionUtc && x.AvailableUtc <= end).ToArray();
        var inputHash = ForexResearchContract.Hash(JsonSerializer.Serialize(new { signalId, direction, decisionUtc, p, costs, equity, dailyLoss,
            prior, future, atr = atrFrames.Where(x => x.AvailableUtc <= end).ToArray() }));
        string requestJson = "", assessmentJson = "";
        ForexSimulationResult Empty(string reason, decimal requested = 0) => new(
            new(signalId, "Incomplete", reason, decisionUtc, null, null, requested, 0, 0, null, null, null, null, null, null),
            end, null, null, null, null, null, inputHash, requestJson, assessmentJson);
        if (direction != TradeDirection.Long) return Empty("ShortExecutionUnsupported");
        if (prior == null || decisionUtc - prior.EventUtc > TimeSpan.FromSeconds(costs.MaximumSnapshotAgeSeconds)
            || !MarketDataSession.IsForexOpen(decisionUtc)) return Empty("NoFreshDecisionQuote");
        var rules = p.HistoricalRules.Where(x => x.ObservedUtc <= decisionUtc && x.Rules.VerifiedAtUtc <= decisionUtc)
            .OrderByDescending(x => x.ObservedUtc).FirstOrDefault();
        if (rules == null) return Empty("NoHistoricalRules");
        var mid = (prior.Bid + prior.Ask) / 2m;
        // This is a fixed counterfactual label policy, not a model forecast or paper-entry approval.
        var request = new ForexRiskRequest(p.InstrumentId, p.StrategyId, direction, decisionUtc, prior.Bid, prior.Ask,
            prior.EventUtc, prior.AvailableUtc, prior.Ask - p.StopPips * costs.PipSize, prior.Ask + p.TargetPips * costs.PipSize,
            p.TargetPips * costs.PipSize, p.MaximumBaseUnits, rules.Contract, rules.ObservedUtc, rules.Rules,
            new(decisionUtc, true, new(equity, "USD"), new(equity, "USD"), new(dailyLoss, "USD"), [], 0), []);
        requestJson = JsonSerializer.Serialize(request);
        var a = risk.Evaluate(request); assessmentJson = JsonSerializer.Serialize(a);
        if (!a.Passed) return Empty("RiskRejected: " + string.Join(" ", a.Reasons));
        var capacity = decimal.Floor(p.FillBaseUnitsPerQuote / rules.Rules.BaseUnitIncrement) * rules.Rules.BaseUnitIncrement;
        if (capacity < rules.Rules.MinimumBaseUnits) return Empty("ScenarioBelowMinimum", a.BaseUnits);
        var ready = decisionUtc.AddMilliseconds(p.LatencyMilliseconds);
        var expires = decisionUtc.AddSeconds(p.EntryTimeoutSeconds);
        var entryLimit = ForexRiskService.RoundPrice(prior.Ask + costs.SlippagePerSidePips * costs.PipSize, true, rules.Rules.PriceBands);
        decimal filled = 0, exited = 0, entryValue = 0, exitValue = 0, fees = 0, highBid = 0;
        decimal? mfe = null, mae = null;
        var stop = a.StopPrice; decimal? pendingStop = null; DateTime? stopReady = null;
        DateTime? firstFill = null, lastExit = null, exitReady = null;
        var reason = ""; var entryCancelled = false; var coverage = true; var previous = prior;
        decimal Commission(decimal units, decimal price) => Math.Max(costs.MinimumCommissionPerOrderUsd, units * price * costs.CommissionPerOrderBps / 10000m);
        foreach (var q in future)
        {
            if (q.AvailableUtc - previous.AvailableUtc > TimeSpan.FromSeconds(p.MaximumQuoteGapSeconds)
                || q.AvailableUtc - q.EventUtc > TimeSpan.FromSeconds(costs.MaximumSnapshotAgeSeconds)
                || q.Id[..q.Id.LastIndexOf(':')] != previous.Id[..previous.Id.LastIndexOf(':')]
                || !MarketDataSession.IsForexOpen(q.EventUtc)) { coverage = false; break; }
            previous = q;
            if (stopReady <= q.AvailableUtc && pendingStop.HasValue) { stop = pendingStop.Value; pendingStop = null; stopReady = null; }
            var deadline = firstFill?.AddSeconds(p.HoldingSeconds);
            if (firstFill.HasValue && reason is "" or "Target" && q.AvailableUtc >= deadline)
            { reason = "Timer"; exitReady = deadline!.Value.AddMilliseconds(p.LatencyMilliseconds); entryCancelled = true; }
            if (filled > exited)
            {
                var average = entryValue / filled;
                var excursion = (q.Bid - average) / average * 10000m;
                mfe = Math.Max(mfe ?? 0m, excursion); mae = Math.Max(mae ?? 0m, -excursion);
                highBid = Math.Max(highBid, q.Bid);
                if (reason is "" or "Target" && q.Bid <= stop) { reason = "Stop"; exitReady = q.AvailableUtc.AddMilliseconds(p.LatencyMilliseconds); entryCancelled = true; }
                if (reason == "" && p.Exit.Mode == ExitStrategyMode.FixedBracket && q.Bid > a.TargetPrice)
                { reason = "Target"; exitReady = q.AvailableUtc.AddMilliseconds(p.LatencyMilliseconds); entryCancelled = true; }
                if (reason != "" && q.AvailableUtc >= exitReady && (reason != "Target" || q.Bid >= a.TargetPrice))
                {
                    var units = Math.Min(capacity, filled - exited);
                    if (units < rules.Rules.MinimumBaseUnits) { coverage = false; reason = "UnresolvedResidual"; break; }
                    var price = reason == "Target" ? a.TargetPrice : ForexRiskService.RoundPrice(q.Bid - costs.SlippagePerSidePips * costs.PipSize, false, rules.Rules.PriceBands);
                    exited += units; exitValue += units * price; fees += Commission(units, price); lastExit = q.AvailableUtc;
                    if (exited == filled) break;
                }
                if (reason == "" && p.Exit.Mode == ExitStrategyMode.AtrTrailing && pendingStop == null)
                {
                    var initialRisk = average - a.StopPrice;
                    var r = initialRisk > 0 ? (q.Bid - average) / initialRisk : 0;
                    var atr = atrFrames.LastOrDefault(x => x.AvailableUtc <= q.AvailableUtc && q.AvailableUtc - x.AvailableUtc <= TimeSpan.FromSeconds(costs.MaximumSnapshotAgeSeconds));
                    var proposed = stop;
                    if (r >= p.Exit.BreakEvenTriggerR) proposed = Math.Max(proposed, average + p.Exit.BreakEvenOffsetR * initialRisk);
                    if (r >= p.Exit.TrailingActivationR && atr?.Atr is > 0) proposed = Math.Max(proposed, highBid - atr.Atr * p.Exit.TrailingAtrMultiplier);
                    proposed = ForexRiskService.RoundPrice(proposed, false, rules.Rules.PriceBands);
                    if (proposed > stop && proposed < q.Bid) { pendingStop = proposed; stopReady = q.AvailableUtc.AddMilliseconds(p.LatencyMilliseconds); }
                }
            }
            if (!entryCancelled && filled < a.BaseUnits && q.AvailableUtc >= ready && q.AvailableUtc <= expires)
            {
                var price = ForexRiskService.RoundPrice(q.Ask + costs.SlippagePerSidePips * costs.PipSize, true, rules.Rules.PriceBands);
                if (price <= entryLimit)
                {
                    var units = Math.Min(capacity, a.BaseUnits - filled);
                    if (units < rules.Rules.MinimumBaseUnits) { entryCancelled = true; continue; }
                    filled += units; entryValue += units * price; fees += Commission(units, price); firstFill ??= q.AvailableUtc;
                }
            }
            if (q.AvailableUtc > expires) entryCancelled = true;
        }
        var complete = coverage && filled > 0 && filled == exited;
        var horizonAt = firstFill?.AddSeconds(p.HoldingSeconds);
        var horizon = horizonAt.HasValue ? quotes.FirstOrDefault(x => x.AvailableUtc >= horizonAt && x.AvailableUtc <= horizonAt.Value.AddSeconds(p.MaximumQuoteGapSeconds)) : null;
        if (horizon != null)
        {
            var covered = quotes.Where(x => x.AvailableUtc >= prior.AvailableUtc && x.AvailableUtc <= horizon.AvailableUtc).ToArray();
            if (covered.Any(x => x.AvailableUtc - x.EventUtc > TimeSpan.FromSeconds(costs.MaximumSnapshotAgeSeconds))
                || covered.Zip(covered.Skip(1)).Any(x => x.Second.AvailableUtc - x.First.AvailableUtc > TimeSpan.FromSeconds(p.MaximumQuoteGapSeconds)
                    || x.First.Id[..x.First.Id.LastIndexOf(':')] != x.Second.Id[..x.Second.Id.LastIndexOf(':')])) horizon = null;
        }
        var gross = complete ? exitValue - entryValue : (decimal?)null;
        var buffer = entryValue * costs.SafetyBufferBps / 10000m;
        var net = gross - fees - buffer;
        var result = new ReplayTradeResult(signalId, complete ? "Closed" : "Incomplete", coverage ? (reason == "" ? "NoFillOrOpenAtEnd" : reason) : "InsufficientCoverage: " + reason,
            decisionUtc, firstFill, lastExit, a.BaseUnits, filled, exited, filled > 0 ? entryValue / filled : null,
            exited > 0 ? exitValue / exited : null, gross, fees, net,
            horizon != null && filled > 0 ? (horizon.Bid - entryValue / filled) / (entryValue / filled) * 10000m : null);
        return new(result, end, complete ? gross / entryValue * 10000m : null, complete ? (fees + buffer) / entryValue * 10000m : null,
            complete ? net / entryValue * 10000m : null, complete ? mfe ?? 0m : null, complete ? mae ?? 0m : null,
            inputHash, requestJson, assessmentJson);
    }
}
