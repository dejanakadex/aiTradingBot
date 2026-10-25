using TradingBot.Application.Configuration;
using TradingBot.Infrastructure.Interfaces;
using TradingBot.Infrastructure.Services;

namespace TradingBot.Infrastructure.Options;

/// <summary>Explicit historical execution scenarios. Never establishes broker permission.</summary>
public sealed class ForexResearchSettings
{
    public bool AutomaticPaperSignalsEnabled { get; set; }
    public ForexResearchProfile[] Profiles { get; set; } = [];
}

public sealed class ForexResearchProfile
{
    public string InstrumentId { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public int IntervalSeconds { get; set; } = 5;
    public int HoldingSeconds { get; set; } = 60;
    public int EntryTimeoutSeconds { get; set; } = 5;
    public int LatencyMilliseconds { get; set; } = 250;
    public int MaximumQuoteGapSeconds { get; set; } = 3;
    public decimal StopPips { get; set; } = 10;
    public decimal TargetPips { get; set; } = 30;
    public decimal MaximumBaseUnits { get; set; }
    public decimal StartingEquityUsd { get; set; }
    // Explicit synthetic liquidity cap; IBKR quote sizes are not proved executable base-unit volume.
    public decimal FillBaseUnitsPerQuote { get; set; }
    public string FillAssumptionSource { get; set; } = "";
    public ExitStrategySettings Exit { get; set; } = new();
    public ForexBrokerRulesSnapshot[] HistoricalRules { get; set; } = [];
}
