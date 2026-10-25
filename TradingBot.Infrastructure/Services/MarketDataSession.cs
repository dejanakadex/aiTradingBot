using TradingBot.Application.Configuration;
using TradingBot.Domain.Enums;

namespace TradingBot.Infrastructure.Services;

/// <summary>Collection calendar, independent of order permissions. Holiday exceptions remain coverage gaps.</summary>
public static class MarketDataSession
{
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

    public static bool IsForexOpen(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
        var time = local.TimeOfDay;
        return local.DayOfWeek switch
        {
            DayOfWeek.Saturday => false,
            DayOfWeek.Sunday => time >= new TimeSpan(17, 15, 0),
            DayOfWeek.Friday => time < new TimeSpan(17, 0, 0),
            _ => time < new TimeSpan(17, 0, 0) || time >= new TimeSpan(17, 15, 0)
        };
    }

    public static bool IsOpen(ConfiguredInstrument instrument, DateTime utc, TradingSettings trading, bool regularSessionOnly)
    {
        if (instrument.InstrumentType == InstrumentType.Forex) return IsForexOpen(utc);
        if (!regularSessionOnly) return true;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Eastern);
        return local.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && local.TimeOfDay >= new TimeSpan(trading.TradingStartHourNewYork, trading.TradingStartMinuteNewYork, 0)
            && local.TimeOfDay < new TimeSpan(trading.TradingEndHourNewYork, trading.TradingEndMinuteNewYork, 0);
    }

    public static bool HasForexBarBetween(DateTime start, DateTime end, TimeSpan interval)
    {
        for (var at = start; at < end; at = at.Add(interval))
            if (IsForexOpen(at)) return true;
        return false;
    }

    public static IEnumerable<(DateTime Start, DateTime End)> ForexOpenWindows(DateTime start, DateTime end)
    {
        DateTime? open = null;
        for (var at = start; at < end;)
        {
            if (IsForexOpen(at)) open ??= at;
            else if (open.HasValue) { yield return (open.Value, at); open = null; }
            var next = new DateTime(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc).AddMinutes(1);
            at = next < end ? next : end;
        }
        if (open.HasValue) yield return (open.Value, end);
    }
}
