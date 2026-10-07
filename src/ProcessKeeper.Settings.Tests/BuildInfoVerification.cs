using System.Globalization;
using ProcessKeeper.Core;

internal static class BuildInfoVerification
{
    internal static void Run(Action<bool, string> check)
    {
        check(ReleaseIdentity.DisplayVersion("1.7.2") == "1.7.1v2", "v2 revision has a friendly label with numeric update identity");
        check(ReleaseIdentity.DisplayVersion("1.7.2+source") == "1.7.1v2", "source hash metadata does not change the friendly revision label");
        check(ReleaseIdentity.DisplayVersion("1.7.1") == "1.7.1" && ReleaseIdentity.DisplayVersion("1.7.2-preview") == "1.7.2-preview", "unrelated and preview versions retain their own identity");
        check(UpdateVersion.TryParse(ReleaseIdentity.NumericVersion, out var numeric) && UpdateVersion.TryParse("1.7.1", out var prior) && numeric!.CompareTo(prior) > 0, "v2 numeric version is newer for retained strict SemVer update clients");
        var utc = DateTimeOffset.ParseExact(BuildInfo.BuildDateUtc, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var utc8 = DateTimeOffset.ParseExact(BuildInfo.BuildDateUtc8, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        check(utc == utc8 && utc8.Offset == TimeSpan.FromHours(8), "UTC and default UTC+8 build metadata describe the same second");
        check(BuildInfo.FormatBuildDate(null) == utc8.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC+8", "unavailable system time zone falls back to compact UTC+8");
        foreach (var example in new (int Minutes, string Label)[] { (-720, "UTC-12"), (-300, "UTC-5"), (-210, "UTC-3:30"), (-30, "UTC-0:30"), (0, "UTC+0"), (330, "UTC+5:30"), (345, "UTC+5:45"), (480, "UTC+8"), (765, "UTC+12:45"), (840, "UTC+14") })
        {
            var zone = TimeZoneInfo.CreateCustomTimeZone("Fixture" + example.Minutes, TimeSpan.FromMinutes(example.Minutes), "Fixture", "Fixture");
            var shown = TimeZoneInfo.ConvertTime(utc, zone);
            check(BuildInfo.FormatBuildDate(zone) == shown.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + example.Label,
                "build date retains actual timezone, instant and second: " + example.Label);
        }
        var local = TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local);
        check(BuildInfo.DisplayBuildDate == TimeDisplay.Format(local), "build date uses current computer timezone rather than always UTC+8");
        var daylight = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromMinutes(30),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 1, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 12, 31));
        var daylightZone = TimeZoneInfo.CreateCustomTimeZone("FixtureHalfHourDST", TimeSpan.FromHours(10), "Fixture", "Fixture standard", "Fixture daylight", new[] { daylight });
        check(BuildInfo.FormatBuildDate(daylightZone) == TimeDisplay.Format(TimeZoneInfo.ConvertTime(utc, daylightZone)), "build date honors seasonal half-hour timezone adjustment");
    }
}
