using System.Globalization;
using ProcessKeeper.Core;

internal static class BuildInfoVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var utc = DateTimeOffset.ParseExact(BuildInfo.BuildDateUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var utc8 = DateTimeOffset.ParseExact(BuildInfo.BuildDateUtc8, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        check(utc == utc8 && utc8.Offset == TimeSpan.FromHours(8), "UTC and default UTC+8 build metadata describe the same second");
        check(BuildInfo.FormatBuildDate(null) == utc8.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture), "unavailable system time zone falls back to UTC+8");
        foreach (var hours in new[] { -12d, -5d, 0d, 5.5d, 8d, 14d })
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture" + hours, TimeSpan.FromHours(hours), "Fixture", "Fixture");
            var shown = DateTimeOffset.ParseExact(BuildInfo.FormatBuildDate(zone), "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            check(shown == utc && shown.Offset == zone.BaseUtcOffset && shown.Second == utc.Second,
                "build date changes display zone without changing its instant or second: " + hours);
        }
    }
}
