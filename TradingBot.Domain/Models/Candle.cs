using System;
using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models
{
    public sealed class Candle
    {
        public string Symbol { get; }
        public string InstrumentId { get; }
        public Timeframe Timeframe { get; }
        public DateTime TimestampUtc { get; }
        public DateTime ReceivedTimeUtc { get; }
        public string Source { get; }
        public bool IsFinal { get; }
        public MarketDataQualityStatus QualityStatus { get; }
        public decimal Open { get; }
        public decimal High { get; }
        public decimal Low { get; }
        public decimal Close { get; }
        public decimal Volume { get; }

        public Candle(
            string symbol,
            Timeframe timeframe,
            DateTime timestampUtc,
            decimal open,
            decimal high,
            decimal low,
            decimal close,
            decimal volume,
            string? instrumentId = null,
            DateTime? receivedTimeUtc = null,
            string? source = null,
            bool isFinal = true,
            MarketDataQualityStatus qualityStatus = MarketDataQualityStatus.Healthy)
        {
            if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
            if (high < low) throw new ArgumentException("high must be >= low");
            if (open < 0 || high < 0 || low < 0 || close < 0) throw new ArgumentOutOfRangeException("prices must be non-negative");
            if (volume < 0) throw new ArgumentOutOfRangeException(nameof(volume));

            Symbol = symbol;
            InstrumentId = string.IsNullOrWhiteSpace(instrumentId) ? symbol : instrumentId.Trim();
            Timeframe = timeframe;
            TimestampUtc = timestampUtc.Kind switch
            {
                DateTimeKind.Utc => timestampUtc,
                DateTimeKind.Unspecified => DateTime.SpecifyKind(timestampUtc, DateTimeKind.Utc),
                _ => timestampUtc.ToUniversalTime()
            };
            ReceivedTimeUtc = receivedTimeUtc.HasValue
                ? receivedTimeUtc.Value.Kind switch
                {
                    DateTimeKind.Utc => receivedTimeUtc.Value,
                    DateTimeKind.Unspecified => DateTime.SpecifyKind(receivedTimeUtc.Value, DateTimeKind.Utc),
                    _ => receivedTimeUtc.Value.ToUniversalTime()
                }
                : TimestampUtc;
            Source = string.IsNullOrWhiteSpace(source) ? "Legacy" : source.Trim();
            IsFinal = isFinal;
            QualityStatus = qualityStatus;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }
    }
}
