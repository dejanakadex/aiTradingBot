using System.Collections.Concurrent;
using IBApi;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Options;

namespace TradingBot.Infrastructure.Services;

public sealed partial class IbkrBrokerService
{
    private readonly TradingSettings _fxTrading;
    private readonly ForexExecutionSettings _fxExecution;
    private readonly SemaphoreSlim _marketRuleGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<ForexPriceBand[]>> _marketRules = new();
    private readonly ConcurrentDictionary<int, OrderRequestDto> _fxSubmittedOrders = new();

    internal static string CanonicalBrokerSymbol(string? symbol, string? securityType, string? currency) =>
        securityType == "CASH" ? $"{symbol}{currency}".ToUpperInvariant() : symbol ?? "";

    public async Task<ForexBrokerRulesSnapshot> GetForexOrderRulesAsync(ConfiguredInstrument instrument, CancellationToken cancellationToken = default)
    {
        var profile = _fxExecution.Profiles.SingleOrDefault(x => x.InstrumentId == instrument.InstrumentId);
        if (profile == null || profile.ReviewedAtUtc.Kind != DateTimeKind.Utc || profile.ReviewedAtUtc > DateTime.UtcNow
            || DateTime.UtcNow - profile.ReviewedAtUtc > TimeSpan.FromHours(24) || string.IsNullOrWhiteSpace(profile.EvidenceSource)
            || profile.MinimumBaseUnits <= 0 || profile.MaximumBaseUnits < profile.MinimumBaseUnits)
            throw new InvalidOperationException("Fresh reviewed FX venue size limits are required.");
        var probe = await ProbeContractAsync(instrument, cancellationToken).ConfigureAwait(false);
        var metadata = probe.Metadata;
        if (probe.Status != "Confirmed" || probe.ContractId is not > 0 || metadata?.MinimumSize is not > 0
            || metadata.SizeIncrement is not > 0 || metadata.MinimumTick is not > 0)
            throw new InvalidOperationException("FX contract or size/tick metadata is unavailable.");
        var venues = (metadata.ValidExchanges ?? "").Split(',', StringSplitOptions.TrimEntries);
        var ids = (metadata.MarketRuleIds ?? "").Split(',', StringSplitOptions.TrimEntries);
        var index = Array.IndexOf(venues, "IDEALPRO");
        if (index < 0 || ids.Length != venues.Length || !int.TryParse(ids[index], out var ruleId) || ruleId <= 0)
            throw new InvalidOperationException("IDEALPRO market-rule identity is unavailable.");
        await _marketRuleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = NewTcs<ForexPriceBand[]>();
            _marketRules[ruleId] = completion;
            GetConnectedClient().reqMarketRule(ruleId);
            var bands = await completion.Task.WaitAsync(BrokerResponseTimeout, cancellationToken).ConfigureAwait(false);
            if (bands.Length == 0 || bands[0].LowEdge != 0 || bands.Any(x => x.Increment <= 0)
                || bands.Zip(bands.Skip(1)).Any(x => x.First.LowEdge >= x.Second.LowEdge))
                throw new InvalidOperationException("Broker returned invalid FX price bands.");
            return new(probe, DateTime.UtcNow, new(probe.ContractId.Value, profile.ReviewedAtUtc, profile.EvidenceSource,
                Math.Max(profile.MinimumBaseUnits, metadata.MinimumSize.Value), profile.MaximumBaseUnits,
                metadata.SizeIncrement.Value, ruleId, bands));
        }
        finally { _marketRules.TryRemove(ruleId, out _); _marketRuleGate.Release(); }
    }

    public override void marketRule(int marketRuleId, PriceIncrement[] priceIncrements)
    {
        if (_marketRules.TryGetValue(marketRuleId, out var completion))
            completion.TrySetResult(priceIncrements.Select(x => new ForexPriceBand(ToDecimal(x.LowEdge), ToDecimal(x.Increment))).ToArray());
    }

    public override void marketRuleProtoBuf(IBApi.protobuf.MarketRule rule) =>
        marketRule(rule.MarketRuleId, rule.PriceIncrements.Select(x => new PriceIncrement { LowEdge = x.LowEdge, Increment = x.Increment }).ToArray());

    private async Task<Contract> BuildOperationContractAsync(OrderRequestDto request, string? modifyingId, CancellationToken cancellationToken)
    {
        var instrument = ResolveInstrument(request.Symbol);
        if (instrument.InstrumentType != InstrumentType.Forex) return IbkrContractFactory.Create(instrument);
        if (!_fxTrading.ForexPaperExecutionEnabled || _fxTrading.OperatingMode != TradingOperatingMode.PaperTrading || instrument.Currency != "USD")
            throw new NotSupportedException("FX orders require explicit paper opt-in and a USD-quoted pair; live and short entries remain disabled.");
        if (!_managedAccountsReceived || _configuredAccountRecognized != true || _options.AccountId?.StartsWith("DU", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("FX orders require a recognized configured paper account.");
        var snapshot = await GetForexOrderRulesAsync(instrument, cancellationToken).ConfigureAwait(false);
        ValidateForexOrder(request, snapshot.Rules);
        if (request.Side == "BUY")
        {
            if (!instrument.TradingEnabled || request.Type != "LIMIT" || request.Role is not ("LimitBuy" or "BracketEntry"))
                throw new InvalidOperationException("FX entry must be an enabled managed long limit order.");
        }
        else if (request.Side == "SELL")
        {
            var parentKnown = modifyingId == null && request.Role is "ProtectiveStop" or "TakeProfit"
                && int.TryParse(request.ParentBrokerOrderId, out var parentId)
                && _fxSubmittedOrders.TryGetValue(parentId, out var parent) && parent.Symbol == request.Symbol
                && parent.Side == "BUY" && request.Quantity <= parent.Quantity
                && _openOrders.TryGetValue(parentId, out var parentState) && parentState.FilledQuantity == 0;
            if (!parentKnown)
            {
                var positions = await GetPositionsAsync(_options.AccountId!, cancellationToken).ConfigureAwait(false);
                var quantity = positions.Where(x => x.Symbol == instrument.Symbol).Sum(x => x.Quantity);
                var alreadyFilled = int.TryParse(modifyingId, out var orderId) && _openOrders.TryGetValue(orderId, out var order) ? order.FilledQuantity : 0m;
                if (request.Quantity - alreadyFilled > quantity || quantity <= 0)
                    throw new InvalidOperationException("FX SELL is restricted to the confirmed long position; short entry is disabled.");
            }
        }
        else throw new InvalidOperationException("Unsupported FX order side.");
        var contract = IbkrContractFactory.Create(instrument);
        contract.ConId = snapshot.Contract.ContractId!.Value;
        return contract;
    }

    internal static void ValidateForexOrder(OrderRequestDto request, ForexOrderRules rules)
    {
        if (request.Quantity < rules.MinimumBaseUnits || request.Quantity > rules.MaximumBaseUnits
            || rules.BaseUnitIncrement <= 0 || request.Quantity % rules.BaseUnitIncrement != 0)
            throw new InvalidOperationException("FX order quantity violates reviewed base-unit rules; do not round an exit up.");
        foreach (var price in new[] { request.LimitPrice, request.StopPrice }.Where(x => x.HasValue))
            if (price <= 0 || ForexRiskService.RoundPrice(price!.Value, false, rules.PriceBands) != price.Value)
                throw new InvalidOperationException("FX order price is off the broker price grid.");
    }

    public override void execDetailsProtoBuf(IBApi.protobuf.ExecutionDetails data)
    {
        if (data.Contract == null || data.Execution == null) return;
        var e = data.Execution;
        execDetails(data.ReqId, new Contract { Symbol = data.Contract.Symbol, SecType = data.Contract.SecType,
            Currency = data.Contract.Currency, Exchange = data.Contract.Exchange, ConId = data.Contract.ConId },
            new Execution { OrderId = e.OrderId, ExecId = e.ExecId, Time = e.Time, AcctNumber = e.AcctNumber,
                Side = e.Side, CumQty = ToDecimal(e.CumQty), Shares = ToDecimal(e.Shares), AvgPrice = e.AvgPrice, Price = e.Price });
    }

    public override void commissionAndFeesReportProtoBuf(IBApi.protobuf.CommissionAndFeesReport data) =>
        commissionAndFeesReport(new CommissionAndFeesReport { ExecId = data.ExecId, CommissionAndFees = data.CommissionAndFees, Currency = data.Currency });
}
