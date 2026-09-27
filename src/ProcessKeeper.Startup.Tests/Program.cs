using ProcessKeeper.App;
using System.Text.Json;

ProcessKeeper.Core.L.Language = "zh-Hans";

var directory = Path.Combine(Environment.GetEnvironmentVariable("PROCESSKEEPER_STARTUP_TEST_ROOT") ??
    Path.Combine(Path.GetTempPath(), "ProcessKeeper.Startup.Tests"), Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var assertions = 0;
void Check(bool condition, string label) { assertions++; if (!condition) throw new Exception(label); }

var progress = new OnboardingProgress();
Check(progress.PageIndex == 0 && !progress.CanGoBack && !progress.IsLastPage, "First page has no back");
progress.Back(); Check(progress.PageIndex == 0, "First page back is inert");
progress.Next(); Check(progress.PageIndex == 1 && progress.CanGoBack, "Second page can go back");
progress.Back(); Check(progress.PageIndex == 0, "Second returns to first");
progress.Next(); progress.Next(); Check(progress.PageIndex == 2 && progress.CanGoBack, "Third can go back");
progress.Back(); Check(progress.PageIndex == 1, "Third returns to second");
progress.Next(); progress.Next(); Check(progress.PageIndex == 3 && progress.CanGoBack, "Fourth page can go back");
progress.Back(); Check(progress.PageIndex == 2, "Fourth returns to third");
progress.Next(); progress.Next(); Check(progress.PageIndex == 4 && !progress.CanGoBack && progress.IsLastPage, "Final page has no back");
progress.Back(); Check(progress.PageIndex == 4, "Final back is inert");
progress.Next(); Check(progress.PageIndex == 4, "Final next cannot overflow");

var store = new OnboardingStore(directory);
Check(!store.IsCompleted(out var warning) && warning is null, "Missing state shows first-run");
Check(!File.Exists(store.FilePath), "Reading does not create state");
Check(!Directory.EnumerateFiles(directory).Any(), "Walking pages cannot persist completion");
store.Complete();
Check(store.IsCompleted(out warning) && warning is null, "Completion round trip");
using (var document = JsonDocument.Parse(File.ReadAllText(store.FilePath)))
    Check(document.RootElement.GetProperty("Version").GetInt32() == 1, "State is versioned");
store.Complete();
Check(store.IsCompleted(out warning), "Atomic existing-file replacement");
Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "No temporary file remains");

foreach (var text in new[]
{
    "", "[]", "null", "{", "{}", "{\"Version\":2,\"Completed\":true}",
    "{\"Version\":\"1\",\"Completed\":true}", "{\"Version\":1,\"Completed\":\"true\"}",
    "{\"Version\":1,\"Completed\":true,\"Completed\":false}",
    "{\"Version\":1,\"Completed\":true,\"Future\":1}", new string(' ', 4097)
})
{
    File.WriteAllText(store.FilePath, text);
    Check(!store.IsCompleted(out warning) && warning is not null, "Invalid state shows first-run with warning");
    Check(File.ReadAllText(store.FilePath) == text, "Invalid state is preserved");
}
File.WriteAllText(store.FilePath, "{\"Version\":1,\"Completed\":false}");
Check(!store.IsCompleted(out warning) && warning is null, "Explicit unfinished state remains first-run");
store.Complete();
Check(store.IsCompleted(out warning), "Finish replaces damaged/unfinished state");
using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    Check(!store.IsCompleted(out warning) && warning is not null, "Locked state is not considered finished");
    var failed = false;
    try { store.Complete(); } catch (IOException) { failed = true; }
    Check(failed, "Locked state surfaces save failure");
}
Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed save removes only its temporary file");
Check(store.IsCompleted(out warning), "Failed save preserves prior completed state");
var blocked = Path.Combine(directory, "not-a-directory");
File.WriteAllText(blocked, "keep");
var blockedStore = new OnboardingStore(blocked);
var blockedFailed = false;
try { blockedStore.Complete(); } catch (IOException) { blockedFailed = true; }
Check(blockedFailed && File.ReadAllText(blocked) == "keep", "Save failure preserves unrelated path");
Console.WriteLine($"STARTUP TESTS PASS | {assertions} assertions | isolated root: {directory}");
