using System.Globalization;
using ProcessKeeper.Core;

internal static class TimeDisplayVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var examples = new (int Minutes, string Text)[]
        {
            (0, "UTC+0"), (480, "UTC+8"), (-300, "UTC-5"), (330, "UTC+5:30"), (345, "UTC+5:45"),
            (-210, "UTC-3:30"), (765, "UTC+12:45"), (-30, "UTC-0:30"), (30, "UTC+0:30"), (-720, "UTC-12"),
            (-840, "UTC-14"), (840, "UTC+14"), (1, "UTC+0:01"), (-1, "UTC-0:01")
        };
        foreach (var example in examples)
        {
            var offset = TimeSpan.FromMinutes(example.Minutes);
            check(TimeDisplay.FormatOffset(offset) == example.Text, "compact offset retains actual minutes: " + example.Text);
            var date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, offset);
            check(TimeDisplay.Format(date) == "2026-01-02 03:04:05 " + example.Text, "timestamp uses invariant date and offset: " + example.Text);
            check(TimeDisplay.Format(date, true) == "2026-01-02 03:04:05.678 " + example.Text, "optional milliseconds are retained: " + example.Text);
        }
        foreach (var offset in new[] { TimeSpan.FromSeconds(1), TimeSpan.FromHours(15), TimeSpan.MinValue, TimeSpan.MaxValue })
        {
            bool rejected = false;
            try { _ = TimeDisplay.FormatOffset(offset); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "non-minute or unsupported offset rejected without overflow");
        }
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            check(TimeDisplay.Format(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8))) == "2026-01-02 03:04:05 UTC+8",
                "current culture does not alter timestamp calendar or UTC notation");
        }
        finally { CultureInfo.CurrentCulture = culture; }
        var instant = new DateTimeOffset(2026, 1, 1, 0, 15, 16, TimeSpan.Zero);
        check(TimeDisplay.Format(instant.ToOffset(TimeSpan.FromHours(-5))) == "2025-12-31 19:15:16 UTC-5", "negative-zone date rollover remains correct");
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1));
        var summerZone = TimeZoneInfo.CreateCustomTimeZone("FixtureDST", TimeSpan.FromHours(-5), "Fixture", "Fixture standard", "Fixture daylight", new[] { adjustment });
        check(TimeDisplay.Format(TimeZoneInfo.ConvertTime(instant, summerZone)) == "2025-12-31 19:15:16 UTC-5", "winter uses timezone's actual standard offset");
        check(TimeDisplay.Format(TimeZoneInfo.ConvertTime(instant.AddMonths(6), summerZone)) == "2026-06-30 20:15:16 UTC-4", "summer uses timezone's actual daylight offset");
        var local = instant.ToLocalTime();
        check(TimeDisplay.Format(local) == local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + TimeDisplay.FormatOffset(local.Offset),
            "current computer timezone remains the display input");

        var buffer = new ActivityLogBuffer();
        var persisted = buffer.Append("message has +08:00 and UTC+8\n2026-01-02 03:04:05.678 +05:45  body line", new(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromMinutes(345)));
        check(persisted.StartsWith("2026-01-02 03:04:05.678 +05:45  ", StringComparison.Ordinal), "new log storage keeps its established parseable timestamp");
        check(buffer.Render().StartsWith("2026-01-02 03:04:05.678 UTC+5:45  ", StringComparison.Ordinal), "history display retains original fractional timezone");
        check(buffer.Render().IndexOf("message has +08:00 and UTC+8", StringComparison.Ordinal) >= 0 && buffer.Render().IndexOf("    2026-01-02 03:04:05.678 +05:45  body line", StringComparison.Ordinal) >= 0,
            "history transformation does not rewrite message bodies or continuation timestamps");
        var loaded = new ActivityLogBuffer(); loaded.Load(persisted);
        check(loaded.Render() == buffer.Render(), "loaded old-format event and new event share compact rendering");
        loaded.Load("12-31 23:59:59  unknown-year-and-zone");
        check(loaded.Render() == "12-31 23:59:59  unknown-year-and-zone", "legacy year and timezone are never invented");
        var bounded = new ActivityLogBuffer(maximumEntries: 400, maximumCharacters: 1024);
        for (int index = 0; index < 400; index++) bounded.Append("fractional-" + index, new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromMinutes(-30)).AddMilliseconds(index));
        check(bounded.Render().Length <= 1024 && bounded.Render().IndexOf("fractional-399", StringComparison.Ordinal) >= 0,
            "longer fractional UTC display respects history character budget and newest entry");
    }
}
