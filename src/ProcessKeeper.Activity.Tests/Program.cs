using ProcessKeeper.Core;

L.Language = "en";
var checks = 0;
void Check(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
TimeDisplayVerification.Run(Check);
void Ordered(string text, params string[] markers)
{
    var previous = -1;
    foreach (var marker in markers)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Check(index > previous, "order: " + marker);
        previous = index;
    }
}
var date = new DateTimeOffset(2026, 9, 27, 12, 34, 56, 789, TimeSpan.FromHours(8));
var buffer = new ActivityLogBuffer();
var serialized = buffer.Append("saved", date);
Check(serialized == "2026-09-27 12:34:56.789 +08:00  saved", "complete invariant timestamp with year and milliseconds");
Check(buffer.Render() == "2026-09-27 12:34:56.789 UTC+8  saved", "human-readable event time uses compact UTC offset without changing storage");
buffer.Append("earlier", date.AddMilliseconds(-1));
buffer.Append("later", date.AddMilliseconds(1));
Ordered(buffer.Render(), "earlier", "saved", "later");
var reload = new ActivityLogBuffer();
reload.Load(buffer.Append("multiline\n2026-09-27 12:34:56.789 +08:00  not-an-event\nlast-line", date) + "\r\n" + serialized);
Check(reload.Count == 2, "timestamp-like continuation remains one event after reload");
Check(reload.Render().Contains("    2026-09-27 12:34:56.789 +08:00  not-an-event"), "continuation indentation preserved");
Ordered(reload.Render(), "multiline", "not-an-event", "last-line", "saved");
buffer = new ActivityLogBuffer();
buffer.Load("2027-01-01 00:00:00.001 +08:00  next-year\n2026-12-31 23:59:59.999 +08:00  previous-year\n    same-event\n2027-01-01 00:00:00.001 +08:00  tie-second");
Ordered(buffer.Render(), "previous-year", "same-event", "next-year", "tie-second");
Check(buffer.Count == 3, "cross-year load keeps multiline event together");
buffer = new ActivityLogBuffer();
buffer.Append("submillisecond-first", date.AddTicks(9_000));
buffer.Append("submillisecond-second", date.AddTicks(1_000));
Ordered(buffer.Render(), "submillisecond-first", "submillisecond-second");
buffer = new ActivityLogBuffer();
buffer.Load("2026-09-27 02:00:00.000 +00:00  utc-later\n2026-09-27 09:00:00.000 +08:00  offset-earlier");
Ordered(buffer.Render(), "offset-earlier", "utc-later");
buffer.Load("12-31 23:59:59  legacy-one\n    legacy-detail\n01-01 00:00:00  legacy-two\n2026-09-27 12:34:56.789 +08:00  new-format");
var rendered = buffer.Render();
Check(rendered.StartsWith("12-31 23:59:59  legacy-one"), "legacy records have no extra heading");
Check(!rendered.Contains("Legacy records |"), "legacy explanation removed from English view");
Ordered(rendered, "12-31 23:59:59  legacy-one", "legacy-detail", "01-01 00:00:00  legacy-two", "new-format");
Check(!rendered.Contains("2026-12-31") && !rendered.Contains("23:59:59.000"), "legacy year and milliseconds are not invented");
Check(buffer.Count == 3, "legacy continuation grouped");
buffer.Load("02-29 11:22:33  leap-year-unknown\n02-30 11:22:33  invalid-date-continuation");
Check(buffer.Count == 1, "legacy leap day recognized without choosing its year");
Check(buffer.Render().Contains("invalid-date-continuation"), "unknown text retained");
buffer = new ActivityLogBuffer(3);
foreach (var n in new[] { 4, 1, 3, 2, 5 }) buffer.Append("entry-" + n, date.AddMilliseconds(n));
Check(buffer.Count == 3, "entry limit respected");
Ordered(buffer.Render(), "entry-3", "entry-4", "entry-5");
Check(!buffer.Render().Contains("entry-1") && !buffer.Render().Contains("entry-2"), "oldest entries removed as whole events");
buffer = new ActivityLogBuffer(maximumCharacters: 512);
serialized = buffer.Append(new string('x', 30_000), date);
Check(serialized.Length > 30_000, "serialized file content remains complete");
Check(buffer.Render().Length <= 512, "single large event has bounded rendered length");
Check(buffer.Render().Contains("Display truncated"), "large event truncation is explicit");
for (var i = 0; i < 30; i++) buffer.Append("message-" + i + new string('y', 90), date.AddMilliseconds(i));
Check(buffer.Render().Length <= 512, "combined character limit respected");
Check(buffer.Render().Contains("message-29"), "most recent event retained");
buffer.Load("");
Check(buffer.Count == 0 && buffer.Render() == "", "empty file clears old view");
var fixture = Environment.GetEnvironmentVariable("PROCESSKEEPER_ACTIVITY_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "ProcessKeeper.Activity.Tests");
Directory.CreateDirectory(fixture);
var path = Path.Combine(fixture, "activity-fixture-" + Guid.NewGuid().ToString("N") + ".log");
try
{
    var newest = "2026-09-27 12:34:56.789 +08:00  newest-complete-event\n    complete-details\n";
    File.WriteAllText(path, "01-01 00:00:00  outside-tail\n" + new string('z', ActivityLogBuffer.MaximumReadBytes + 256) + "\n" + newest);
    var bytesBefore = File.ReadAllBytes(path);
    buffer.LoadFile(path);
    Check(buffer.Count == 1, "bounded file tail discards partial prior event");
    Check(buffer.Render().Contains("newest-complete-event") && buffer.Render().Contains("complete-details"), "tail keeps newest complete multiline event");
    Check(!buffer.Render().Contains("outside-tail"), "file reader excludes content outside bounded tail");
    Check(bytesBefore.AsSpan().SequenceEqual(File.ReadAllBytes(path)), "file loading never rewrites original log");
    File.WriteAllText(path, "12-31 23:59:59  old\n");
    buffer.LoadFile(path);
    var eventText = buffer.Append("new", date);
    File.AppendAllText(path, eventText + Environment.NewLine);
    buffer.LoadFile(path);
    Ordered(buffer.Render(), "12-31 23:59:59  old", "2026-09-27 12:34:56.789 UTC+8  new");
    Check(File.ReadAllText(path).StartsWith("12-31 23:59:59  old\n"), "appending preserves old bytes and format");
}
finally { File.Delete(path); }
L.Language = "zh-Hans";
buffer.Load("01-01 00:00:00  old");
Check(buffer.Render() == "01-01 00:00:00  old", "Simplified Chinese legacy record has no injected explanation");
L.Language = "zh-Hant";
Check(buffer.Render() == "01-01 00:00:00  old", "Traditional Chinese legacy record has no injected explanation");
Console.WriteLine($"Activity log: {checks} checks passed.");
