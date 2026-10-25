using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

internal sealed record IbkrDataProfile(string WhatToShow, int UseRegularHours, bool RequestTrades, string TickSource, string BarSource)
{
    public static IbkrDataProfile For(ConfiguredInstrument instrument) => instrument.InstrumentType == InstrumentType.Forex
        ? new("MIDPOINT", 0, false, ForexQuoteAggregator.QuoteSource, "IBKR.Midpoint")
        : new("TRADES", 1, true, "IBKR.TickByTick", "IBKR");
}
