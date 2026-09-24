using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.DTOs
{
    public static class CanonicalMarketDataIdentity
    {
        public static string ForBar(string instrumentId, MarketBar bar, string normalizedTimeframe)
        {
            if (bar == null) throw new ArgumentNullException(nameof(bar));
            var canonical = string.Join('|',
                instrumentId.Trim().ToUpperInvariant(),
                MarketDataEventKind.Bar,
                normalizedTimeframe.Trim().ToLowerInvariant(),
                ToUtc(bar.TimestampUtc).ToString("O"),
                bar.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                bar.Open.ToString(CultureInfo.InvariantCulture),
                bar.High.ToString(CultureInfo.InvariantCulture),
                bar.Low.ToString(CultureInfo.InvariantCulture),
                bar.Close.ToString(CultureInfo.InvariantCulture),
                bar.Volume.ToString(CultureInfo.InvariantCulture));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }

        private static DateTime ToUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value.ToUniversalTime()
        };
    }
}
