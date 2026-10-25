using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Services;

/// <summary>Shared live/replay feature query. Does not run patterns, models or orders.</summary>
public sealed class ForexFeatureService(ILatestMarketDataService latest, IFeatureEngine features,
    IOptions<TradingSettings> trading, IOptions<ShortAggregateSettings> aggregates)
{
    public MarketFeatures? Compute(string instrumentId, int intervalSeconds, DateTime asOfUtc)
    {
        var input = GetInput(instrumentId, intervalSeconds, asOfUtc);
        return input == null ? null : features.ComputeFeatures(input);
    }

    public CanonicalFeatureInput? GetInput(string instrumentId, int intervalSeconds, DateTime asOfUtc)
    {
        var instrument = trading.Value.GetConfiguredInstruments().Single(x => x.InstrumentId == instrumentId);
        if (instrument.InstrumentType != InstrumentType.Forex) throw new ArgumentException("An FX instrument is required.", nameof(instrumentId));
        var bars = latest.GetCompletedForexAggregates(instrumentId, intervalSeconds, asOfUtc, aggregates.Value.MaxCompletedPerInterval);
        if (bars.Count == 0) return null;
        var timeframe = intervalSeconds switch
        {
            1 => Timeframe.OneSecond, 5 => Timeframe.FiveSeconds, 15 => Timeframe.FifteenSeconds,
            _ => throw new ArgumentOutOfRangeException(nameof(intervalSeconds))
        };
        var candles = bars.Select(bar => new Candle(instrument.Symbol, timeframe, bar.WindowEndUtc,
            bar.Open, bar.High, bar.Low, bar.Close, null, instrument.InstrumentId, bar.AvailableAtUtc,
            $"{ForexQuoteAggregator.CandleSource}.{bar.Version}.{intervalSeconds}s")).ToArray();
        var last = bars[^1];
        return new CanonicalFeatureInput
        {
            Candles = candles, ForexMidpoint = true, AsOfUtc = asOfUtc,
            Bid = last.LastBid, Ask = last.LastAsk, BidTimeUtc = last.LastQuoteTimeUtc, AskTimeUtc = last.LastQuoteTimeUtc
        };
    }
}
