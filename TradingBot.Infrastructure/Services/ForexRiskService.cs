using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

/// <summary>Deterministic, USD-valued FX sizing/cost assessment. No broker, DB, reservation or order calls.
/// FX-9 must refresh and reserve capacity atomically before execution; this snapshot is not a reservation.</summary>
public sealed class ForexRiskService
{
    private readonly ForexRiskSettings _settings;
    private readonly RiskSettings _risk;
    private readonly TradingSettings _trading;

    public ForexRiskService(IOptions<ForexRiskSettings> settings, IOptions<RiskSettings> risk, IOptions<TradingSettings> trading)
    {
        _settings = Copy(settings.Value);
        _risk = Copy(risk.Value);
        _trading = Copy(trading.Value);
    }

    public ForexRiskAssessment Evaluate(ForexRiskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { request, settings = _settings, risk = _risk, trading = _trading })))).ToLowerInvariant();
        try { return EvaluateCore(request) with { InputHash = hash }; }
        catch (RiskInputException ex) { return new() { InputHash = hash, Reasons = [ex.Message] }; }
        catch (OverflowException) { return new() { InputHash = hash, Reasons = ["FX arithmetic exceeds supported decimal range."] }; }
    }

    private ForexRiskAssessment EvaluateCore(ForexRiskRequest r)
    {
        Require(_settings.Validate().Count == 0, "Invalid FX risk configuration.");
        Require(_trading.GetValidationErrors().Count == 0, "Invalid instrument configuration.");
        var instrument = _trading.GetConfiguredInstruments().SingleOrDefault(x => x.InstrumentId == r.InstrumentId);
        Require(instrument != null && instrument.Enabled && instrument.InstrumentType == InstrumentType.Forex
            && instrument.StrategyIds.Contains(r.StrategyId), "An enabled configured CASH instrument/strategy is required.");
        Require(Enum.IsDefined(r.Direction) && instrument!.AllowedDirections.Contains(r.Direction), "FX direction is not permitted.");
        var p = _settings.Profiles.SingleOrDefault(x => x.InstrumentId == r.InstrumentId && x.StrategyId == r.StrategyId);
        Require(p != null, "A matching FX risk profile is required.");
        var profile = p!;
        Require(r.AsOfUtc.Kind == DateTimeKind.Utc, "Assessment time must be UTC.");
        bool Fresh(DateTime time, TimeSpan maxAge) => time.Kind == DateTimeKind.Utc && time <= r.AsOfUtc && r.AsOfUtc - time <= maxAge;
        var maxAge = TimeSpan.FromSeconds(profile.MaximumSnapshotAgeSeconds);
        Require(Fresh(r.QuoteEventUtc, maxAge) && Fresh(r.QuoteReceivedUtc, maxAge), "FX quote is stale, future or not UTC.");
        Require(r.Bid > 0 && r.Ask >= r.Bid && r.StopPrice > 0 && r.TargetPrice > 0
            && r.ExpectedMidpointMove > 0 && r.MaximumBaseUnits > 0, "Invalid FX quote, stop, target, forecast or size cap.");
        var mid = (r.Bid + r.Ask) / 2m;
        Require((r.Ask - r.Bid) / mid * 10000m <= profile.MaximumSpreadBps, "FX spread exceeds profile limit.");

        var rules = r.Rules;
        var metadata = r.Contract?.Metadata;
        Require(r.Contract?.Status == "Confirmed" && r.Contract.MatchCount == 1 && r.Contract.ContractId > 0
            && metadata != null && metadata.Contract == IbkrContractFactory.Describe(instrument!), "FX broker contract is not uniquely confirmed for this instrument.");
        var ruleAge = TimeSpan.FromHours(profile.MaximumRulesAgeHours);
        Require(rules != null && Fresh(r.ContractObservedUtc, ruleAge) && Fresh(rules.VerifiedAtUtc, ruleAge)
            && rules.ContractId == r.Contract!.ContractId && !string.IsNullOrWhiteSpace(rules.EvidenceSource), "Missing, stale or mismatched reviewed FX order rules.");
        var rule = rules!;
        Require(metadata!.MinimumTick > 0 && metadata.MinimumSize > 0 && metadata.SizeIncrement > 0,
            "Broker minimum tick/size/increment is unknown; do not substitute a suggested size or invent an odd-lot rule.");
        Require(rule.MinimumBaseUnits >= metadata.MinimumSize && rule.MaximumBaseUnits >= rule.MinimumBaseUnits
            && rule.BaseUnitIncrement > 0 && rule.BaseUnitIncrement % metadata.SizeIncrement!.Value == 0,
            "Reviewed quantity rules conflict with broker minimum/increment.");
        Require(rule.PriceBands is { Count: > 0 } && rule.PriceBands[0].LowEdge == 0
            && rule.PriceBands.All(x => x.LowEdge >= 0 && x.LowEdge % metadata.MinimumTick!.Value == 0
                && x.Increment >= metadata.MinimumTick && x.Increment % metadata.MinimumTick!.Value == 0)
            && rule.PriceBands.Zip(rule.PriceBands.Skip(1)).All(x => x.First.LowEdge < x.Second.LowEdge), "Invalid or incomplete FX price increment bands.");
        var brokerRuleIds = (metadata.MarketRuleIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var venues = (metadata.ValidExchanges ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var venueIndex = Array.IndexOf(venues, instrument.Exchange);
        Require(brokerRuleIds.Length == 0 ? rule.MarketRuleId == null : rule.MarketRuleId.HasValue
            && brokerRuleIds.Length == venues.Length && venueIndex >= 0
            && brokerRuleIds[venueIndex] == rule.MarketRuleId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Reviewed price bands must identify a broker market rule; MinimumTick alone is insufficient.");
        Require(profile.PipSize >= metadata.MinimumTick && profile.PipSize % metadata.MinimumTick!.Value == 0, "Pip size is incompatible with broker tick.");

        Require(r.Conversions != null && r.Conversions.All(x => x != null)
            && r.Conversions.GroupBy(x => x.Currency).All(x => x.Count() == 1), "Duplicate or missing USD conversion quotes.");
        (decimal Bid, decimal Ask) Rates(string currency)
        {
            Require(currency.Length == 3 && currency.All(c => c is >= 'A' and <= 'Z'), "Currency codes must be three uppercase letters.");
            if (currency == "USD") return (1, 1);
            var q = r.Conversions!.SingleOrDefault(x => x.Currency == currency);
            Require(q != null && q.Bid > 0 && q.Ask >= q.Bid && Fresh(q.EventUtc, maxAge) && Fresh(q.ReceivedUtc, maxAge),
                $"Missing, invalid or stale {currency}/USD conversion quote.");
            return (q!.Bid, q.Ask);
        }
        decimal Asset(CurrencyAmount value) { var q = Rates(value.Currency); return value.Amount * (value.Amount >= 0 ? q.Bid : q.Ask); }
        decimal Exposure(CurrencyAmount value) => value.Amount * Rates(value.Currency).Ask;
        var quoteRates = Rates(instrument!.Currency);
        var basis = instrument.BaseCurrency!;
        var quote = instrument.Currency;
        var portfolio = r.Portfolio;
        Require(portfolio != null && portfolio.Reconciled && Fresh(portfolio.AsOfUtc, maxAge)
            && portfolio.Exposures != null && portfolio.OpenPositionAndOrderCount >= portfolio.Exposures.Count,
            "A fresh, complete, reconciled portfolio including pending capacity is required.");
        Require(_risk.MaximumPositionValue > 0 && _risk.MaximumRiskPerTrade > 0 && _risk.MaximumLeverage > 0
            && _risk.MaximumGrossExposure > 0 && _risk.MaximumNetExposure > 0 && _risk.MaximumInstrumentExposure > 0
            && _risk.MaximumStrategyExposure > 0 && _risk.MaximumDailyLoss > 0 && _risk.MaximumCorrelationGroupExposure > 0,
            "FX assessment requires positive existing risk limits.");
        var equity = Asset(portfolio!.NetLiquidation);
        var buyingPower = Asset(portfolio.AvailableBuyingPower);
        Require(equity > 0 && buyingPower > 0, "No account equity or buying power remains.");
        Require(Asset(portfolio.DailyRealizedPnl) > -_risk.MaximumDailyLoss, "Daily loss limit is exhausted.");
        Require(portfolio.OpenPositionAndOrderCount < _risk.MaximumOpenPositions, "Open position/order limit is exhausted.");
        var exposures = portfolio.Exposures.Select(x => (Item: x, Value: Exposure(x.SignedNotional))).ToArray();
        var gross = exposures.Sum(x => Math.Abs(x.Value));
        var net = exposures.Sum(x => x.Value);
        var direction = (int)r.Direction;
        Require(Math.Abs(net) <= _risk.MaximumNetExposure, "Existing portfolio net exposure exceeds its limit.");
        var capacity = new List<decimal> { _risk.MaximumPositionValue, _risk.MaximumGrossExposure - gross,
            _risk.MaximumNetExposure - direction * net, equity * _risk.MaximumLeverage - gross,
            _risk.MaximumInstrumentExposure - exposures.Where(x => x.Item.InstrumentId == r.InstrumentId).Sum(x => Math.Abs(x.Value)),
            _risk.MaximumStrategyExposure - exposures.Where(x => x.Item.StrategyId == r.StrategyId).Sum(x => Math.Abs(x.Value)) };
        if (instrument.MaximumPositionValue.HasValue) capacity.Add(instrument.MaximumPositionValue.Value);
        foreach (var members in _risk.CorrelationGroups.Values.Where(x => x.Contains(r.InstrumentId) || x.Contains(instrument.Symbol)))
            capacity.Add(_risk.MaximumCorrelationGroupExposure - exposures.Where(x => members.Contains(x.Item.InstrumentId)
                || _trading.GetConfiguredInstruments().Any(i => i.InstrumentId == x.Item.InstrumentId && members.Contains(i.Symbol))).Sum(x => Math.Abs(x.Value)));
        Require(capacity.Min() > 0, "No FX portfolio exposure capacity remains.");
        var currencies = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var exposure in portfolio.Exposures)
        {
            Require(exposure.CurrencyLegs is { Count: > 0 }, "Currency legs are required for every exposure.");
            foreach (var leg in exposure.CurrencyLegs)
                currencies[leg.Currency] = currencies.GetValueOrDefault(leg.Currency) + Exposure(leg);
        }
        Require(currencies.Values.All(x => Math.Abs(x) <= profile.MaximumCurrencyExposureUsd), "Existing currency exposure exceeds profile limit.");

        var entry = RoundPrice(direction == 1 ? r.Ask : r.Bid, direction == 1, rule.PriceBands);
        var stop = RoundPrice(r.StopPrice, direction == -1, rule.PriceBands);
        var target = RoundPrice(r.TargetPrice, direction == -1, rule.PriceBands);
        Require(entry > 0 && stop > 0 && target > 0 && direction * (entry - stop) > 0
            && direction * (target - entry) > 0, "Rounded stop/target is on the wrong side of entry.");
        var notionalPerUnit = entry * quoteRates.Ask;
        // Both legs are valued using the same conservative pair-implied cross rate.
        var currencyPerUnit = entry * quoteRates.Ask;
        capacity.Add(profile.MaximumCurrencyExposureUsd - direction * currencies.GetValueOrDefault(basis));
        capacity.Add(profile.MaximumCurrencyExposureUsd + direction * currencies.GetValueOrDefault(quote));
        Require(capacity.Min() > 0, "No FX currency exposure capacity remains.");
        var maxUnits = Math.Min(Math.Min(r.MaximumBaseUnits, rule.MaximumBaseUnits), capacity.Min() / notionalPerUnit);
        var upperSteps = Math.Floor(maxUnits / rule.BaseUnitIncrement);
        var minimumSteps = Math.Ceiling(rule.MinimumBaseUnits / rule.BaseUnitIncrement);
        var slipPerUnitUsd = 2m * profile.SlippagePerSidePips * profile.PipSize * quoteRates.Ask;
        var safetyPerUnitUsd = notionalPerUnit * profile.SafetyBufferBps / 10000m;
        decimal Commission(decimal quantity)
        {
            // Unknown split sizes: upper bound each possible order by the full notional.
            // max(n * minimum, total percentage) would underestimate an uneven split.
            var entryFee = profile.EntryCommissionOrders * Math.Max(profile.MinimumCommissionPerOrderUsd,
                quantity * notionalPerUnit * profile.CommissionPerOrderBps / 10000m);
            var exitNotional = Math.Max(stop, target) * quantity * quoteRates.Ask;
            var exitFee = profile.ExitCommissionOrders * Math.Max(profile.MinimumCommissionPerOrderUsd,
                exitNotional * profile.CommissionPerOrderBps / 10000m);
            return entryFee + exitFee;
        }
        decimal StopRisk(decimal quantity) => quantity * (Math.Abs(entry - stop) * quoteRates.Ask + slipPerUnitUsd + safetyPerUnitUsd) + Commission(quantity);
        // Monotonic search includes fixed minimum fees; never round up to meet a broker minimum.
        decimal low = 0, high = upperSteps, selected = 0;
        while (low <= high)
        {
            var step = low + Math.Floor((high - low) / 2m);
            var quantity = step * rule.BaseUnitIncrement;
            if (StopRisk(quantity) <= _risk.MaximumRiskPerTrade
                && quantity * notionalPerUnit + Commission(quantity) <= buyingPower)
            { selected = step; low = step + 1; }
            else high = step - 1;
        }
        Require(selected >= minimumSteps, "Risk, costs, buying power or exposure leaves less than broker minimum base units.");
        var units = selected * rule.BaseUnitIncrement;
        var commission = Commission(units);
        var spread = units * (r.Ask - r.Bid) * quoteRates.Ask;
        var rounding = units * Math.Abs(entry - (direction == 1 ? r.Ask : r.Bid)) * quoteRates.Ask;
        var slippage = units * slipPerUnitUsd;
        var cost = commission + spread + rounding + slippage + units * safetyPerUnitUsd;
        var grossProfit = units * Math.Min(r.ExpectedMidpointMove, direction * (target - mid)) * quoteRates.Bid;
        var notional = units * notionalPerUnit;
        var netProfit = grossProfit - cost;
        var edge = netProfit / notional * 10000m;
        return new ForexRiskAssessment
        {
            BaseCurrency = basis, QuoteCurrency = quote, BaseUnits = units, EntryPrice = entry, StopPrice = stop, TargetPrice = target,
            NotionalUsd = notional, StopRiskUsd = StopRisk(units), PipValueUsd = units * profile.PipSize * quoteRates.Ask,
            CommissionUsd = commission, SpreadUsd = spread, SlippageUsd = slippage, TotalCostUsd = cost,
            ExpectedGrossProfitUsd = grossProfit, ExpectedNetProfitUsd = netProfit, ExpectedNetEdgeBps = edge,
            CurrencyLegsUsd = new Dictionary<string, decimal> { [basis] = direction * units * currencyPerUnit, [quote] = -direction * notional },
            Reasons = netProfit > 0 && edge >= profile.MinimumNetEdgeBps ? [] : ["Expected FX edge does not cover round-trip costs and required net edge."]
        };
    }

    // Search valid ticks across bands, including a round crossing a band boundary.
    internal static decimal RoundPrice(decimal price, bool up, IReadOnlyList<ForexPriceBand> bands)
    {
        decimal? best = null;
        for (var i = 0; i < bands.Count; i++)
        {
            var band = bands[i];
            var upper = i + 1 < bands.Count ? bands[i + 1].LowEdge : decimal.MaxValue;
            var ticks = (price - band.LowEdge) / band.Increment;
            decimal candidate;
            if (up) candidate = band.LowEdge + Math.Max(0m, Math.Ceiling(ticks)) * band.Increment;
            else
            {
                var lastTick = i + 1 < bands.Count ? Math.Ceiling((upper - band.LowEdge) / band.Increment) - 1 : decimal.MaxValue;
                candidate = band.LowEdge + Math.Min(Math.Floor(ticks), lastTick) * band.Increment;
            }
            if (candidate < band.LowEdge || candidate >= upper || (up ? candidate < price : candidate > price)) continue;
            best = best == null ? candidate : up ? Math.Min(best.Value, candidate) : Math.Max(best.Value, candidate);
        }
        Require(best.HasValue, "No valid FX price increment for requested price.");
        return best!.Value;
    }

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message) { if (!condition) throw new RiskInputException(message); }
    private sealed class RiskInputException(string message) : Exception(message);
}
