using System.Globalization;

namespace ProcessKeeper.Core;

/// <summary>Human-readable timestamps only. Persistence and wire formats keep their existing ISO offsets.</summary>
public static class TimeDisplay
{
    public static string Format(DateTimeOffset value, bool includeMilliseconds = false) =>
        value.ToString(includeMilliseconds ? "yyyy-MM-dd HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
        " " + FormatOffset(value.Offset);

    public static string FormatOffset(TimeSpan offset)
    {
        // DateTimeOffset and Windows time zones use whole minutes in the range UTC-14 through UTC+14.
        if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14) || offset.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var minutes = (int)(Math.Abs(offset.Ticks) / TimeSpan.TicksPerMinute);
        return "UTC" + (offset < TimeSpan.Zero ? "-" : "+") + (minutes / 60).ToString(CultureInfo.InvariantCulture) +
            (minutes % 60 == 0 ? "" : ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture));
    }
}
