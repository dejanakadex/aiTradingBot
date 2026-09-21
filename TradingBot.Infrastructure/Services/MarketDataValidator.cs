using System;
using Microsoft.Extensions.Options;
using TradingBot.Application.Configuration;
using TradingBot.Application.DTOs;
using TradingBot.Application.Interfaces;

namespace TradingBot.Infrastructure.Services
{
    public sealed class MarketDataValidator : IMarketDataValidator
    {
        private readonly TradingSettings _settings;
        private readonly IClock _clock;

        public MarketDataValidator(IOptions<TradingSettings> settings, IClock clock)
        {
            _settings = settings.Value;
            _clock = clock;
        }

        public bool TryValidate(MarketBar bar, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(bar.Symbol))
            {
                reason = "Symbol is required.";
                return false;
            }

            if (bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m)
            {
                reason = "OHLC prices must be positive.";
                return false;
            }

            if (bar.High < bar.Low)
            {
                reason = "High is below low.";
                return false;
            }

            if (bar.Open > bar.High || bar.Open < bar.Low || bar.Close > bar.High || bar.Close < bar.Low)
            {
                reason = "Open or close is outside high/low range.";
                return false;
            }

            if (bar.Volume < 0m)
            {
                reason = "Volume cannot be negative.";
                return false;
            }

            var timestampUtc = bar.TimestampUtc.Kind == DateTimeKind.Utc ? bar.TimestampUtc : bar.TimestampUtc.ToUniversalTime();
            var now = _clock.UtcNow;
            if (timestampUtc > now.AddSeconds(5))
            {
                reason = "Candle timestamp is in the future.";
                return false;
            }

            if (now - timestampUtc > TimeSpan.FromSeconds(Math.Max(1, _settings.MaximumCandleAgeSeconds)))
            {
                reason = "Candle timestamp is stale.";
                return false;
            }

            return true;
        }
    }
}
