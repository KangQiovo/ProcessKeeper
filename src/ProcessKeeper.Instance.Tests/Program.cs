using System.Diagnostics;
using System.Text.Json;
using ProcessKeeper.App;

if (args.Length >= 3 && args[0] == "--child")
{
    var directory = args[1]; var label = args[2];
    var postRelease = args.Length > 3 ? int.Parse(args[3]) : 0;
    var initDelay = args.Length > 4 ? int.Parse(args[4]) : 0;
    var close = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = new SingleInstanceCoordinator(() => close.TrySetResult(true), directory);
    var result = await coordinator.AcquireAsync(timeout: TimeSpan.FromSeconds(6));
    File.WriteAllText(Path.Combine(directory, label + ".result"), result.ToString());
    if (result == InstanceAcquireResult.Acquired)
    {
        if (initDelay > 0) await Task.Delay(initDelay);
        if (coordinator.IsOwner) File.WriteAllText(Path.Combine(directory, label + ".opened"), "current owner only");
        var lifetime = Stopwatch.StartNew();
        while (!close.Task.IsCompleted && !File.Exists(Path.Combine(directory, label + ".stop")) && lifetime.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(25);
        if (close.Task.IsCompleted)
        {
            File.WriteAllText(Path.Combine(directory, label + ".close"), "cooperative");
            await Task.Delay(200);
        }
        coordinator.Dispose();
        File.WriteAllText(Path.Combine(directory, label + ".released"), "released");
        if (postRelease > 0) await Task.Delay(postRelease);
    }
    return;
}

var testRoot = Environment.GetEnvironmentVariable("PROCESSKEEPER_INSTANCE_TEST_ROOT") ?? Path.GetTempPath();
var root = Path.Combine(testRoot, "instance-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var self = Process.GetCurrentProcess();
var own = self.MainModule!.FileName;
var mappingPath = Environment.GetEnvironmentVariable("PROCESSKEEPER_INSTANCE_FIXTURE_MAP");
var map = mappingPath is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mappingPath))!;
map["own"] = own;
var children = new List<(Process Process, string Directory, string Label)>();
var results = new List<object>();
void Check(bool condition, string name)
{
    results.Add(new { Name = name, Passed = condition });
    Console.WriteLine((condition ? "PASS: " : "FAIL: ") + name);
    if (!condition) throw new Exception("FAIL: " + name);
}
string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
Process Start(string directory, string label, string flavor = "own", int releaseDelay = 0, int initDelay = 0)
{
    Directory.CreateDirectory(directory);
    var start = new ProcessStartInfo(map[flavor])
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        Arguments = "--child " + Quote(directory) + " " + Quote(label) + " " + releaseDelay + " " + initDelay
    };
    var child = Process.Start(start) ?? throw new Exception("Child did not start");
    children.Add((child, directory, label)); return child;
}
async Task<string> Result(string directory, string label)
{
    var file = Path.Combine(directory, label + ".result"); var watch = Stopwatch.StartNew();
    while (!File.Exists(file) && watch.Elapsed < TimeSpan.FromSeconds(12)) await Task.Delay(25);
    if (!File.Exists(file)) throw new Exception("Child result timed out: " + label);
    for (var attempt = 0; attempt < 20; attempt++)
    { try { var value = File.ReadAllText(file); if (value.Length > 0) return value; } catch (IOException) { } await Task.Delay(25); }
    throw new Exception("Child result empty: " + label);
}
async Task Stop(Process process, string directory, string label)
{
    File.WriteAllText(Path.Combine(directory, label + ".stop"), "stop");
    var watch = Stopwatch.StartNew(); while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(35)) await Task.Delay(25);
    if (!process.HasExited) throw new Exception("Owned helper did not exit: " + label);
}
async Task KeepWinner(string label, string current, string candidate)
{
    var directory = Path.Combine(root, label);
    var winner = Start(directory, "winner", current); Check(await Result(directory, "winner") == "Acquired", label + " initial acquires");
    var loser = Start(directory, "loser", candidate); Check(await Result(directory, "loser") == "Superseded", label + " later weaker candidate exits");
    await Task.Delay(220);
    Check(!winner.HasExited && !File.Exists(Path.Combine(directory, "winner.close")), label + " winner is not displaced");
    Check(!File.Exists(Path.Combine(directory, "loser.opened")), label + " loser never opens UI");
    await Stop(loser, directory, "loser"); await Stop(winner, directory, "winner");
}
async Task ReplaceWinner(string label, string current, string candidate)
{
    var directory = Path.Combine(root, label);
    var older = Start(directory, "older", current, releaseDelay: 1000); Check(await Result(directory, "older") == "Acquired", label + " initial acquires");
    var winner = Start(directory, "winner", candidate); Check(await Result(directory, "winner") == "Acquired", label + " stronger candidate acquires");
    Check(File.Exists(Path.Combine(directory, "older.close")), label + " prior owner closes cooperatively");
    Check(File.Exists(Path.Combine(directory, "older.released")) && older.HasExited, label + " waits actual process exit after lease release");
    Check(!winner.HasExited, label + " only winner remains alive"); await Stop(winner, directory, "winner");
}
try
{
    var handoff = Path.Combine(root, "handoff"); Directory.CreateDirectory(handoff);
    File.WriteAllText(Path.Combine(handoff, "broken.json"), "broken");
    File.WriteAllText(Path.Combine(handoff, "stale.json"), "{\"ProcessId\":2147483646,\"StartedUtcTicks\":999999999999999999,\"SessionId\":1,\"ImagePath\":\"E:\\\\Other.exe\",\"Version\":\"99.0.0\",\"IsModern\":true}");
    var first = Start(handoff, "first", releaseDelay: 1000);
    Check(await Result(handoff, "first") == "Acquired", "malformed and stale claims do not block first acquisition");
    var second = Start(handoff, "second"); Check(await Result(handoff, "second") == "Acquired", "equivalent latest instance wins");
    Check(File.Exists(Path.Combine(handoff, "first.close")), "older instance receives cooperative close");
    Check(first.HasExited, "successor waits actual process exit, beyond old claim/lease disposal");
    var third = Start(handoff, "third"); await Task.Delay(60); var newest = Start(handoff, "newest");
    Check(await Result(handoff, "newest") == "Acquired", "latest overlapping equivalent candidate wins");
    Check(await Result(handoff, "third") == "Superseded", "intermediate candidate never acquires UI ownership");
    Check(second.HasExited && third.HasExited && !newest.HasExited, "only newest owned fixture process survives");
    await Stop(newest, handoff, "newest");

    var delayed = Path.Combine(root, "delayed-initialization");
    var starting = Start(delayed, "starting", initDelay: 1500); Check(await Result(delayed, "starting") == "Acquired", "delayed initialization acquired lease");
    var ready = Start(delayed, "ready"); Check(await Result(delayed, "ready") == "Acquired", "successor waits delayed initialization owner to exit");
    Check(!File.Exists(Path.Combine(delayed, "starting.opened")) && starting.HasExited, "revoked delayed callback cannot create UI");
    await Stop(ready, delayed, "ready");

    if (map.ContainsKey("modern17"))
    {
        await KeepWinner("same-version-modern-priority", "modern17", "compat17");
        await KeepWinner("older-modern-cannot-replace", "compat17", "modern16");
        await KeepWinner("older-compat-cannot-replace", "modern17", "compat16");
        await KeepWinner("prerelease-cannot-replace-stable", "modern17", "preview17");
        await KeepWinner("ineligible-architecture-cannot-replace", "modern17", "ineligible20");
        await KeepWinner("forged-unwrapped-metadata-cannot-replace-trusted", "modern17", "spoof99");
        await ReplaceWinner("modern-replaces-same-version-compat", "compat17", "modern17");
        await ReplaceWinner("newer-compatible-version-before-ui-rank", "modern17", "compat18");
        await ReplaceWinner("cross-package-path-equivalent-latest", "modern17", "modern17copy");
        var forged = Path.Combine(root, "forged-ranking");
        var real = Start(forged, "modern", "modern17"); Check(await Result(forged, "modern") == "Acquired", "forged ranking owner acquires");
        using var live = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(forged, "forged.json"), JsonSerializer.Serialize(new
        { ProcessId = live.Id, StartedUtcTicks = live.StartTime.ToUniversalTime().Ticks, SessionId = live.SessionId,
          ImagePath = live.MainModule!.FileName, Version = "99.0.0", IsModern = true, ContextId = "fake-context" }));
        await Task.Delay(400);
        Check(!File.Exists(Path.Combine(forged, "modern.close")), "untrusted JSON version and UI rank never outrank live metadata");
        await Stop(real, forged, "modern");
        var duplicate = Path.Combine(root, "duplicate-locator-evidence");
        var authenticated = Start(duplicate, "trusted", "modern17"); Check(await Result(duplicate, "trusted") == "Acquired", "duplicate locator initial authenticated fixture acquires");
        File.WriteAllText(Path.Combine(duplicate, "zz-forged-locator.json"), JsonSerializer.Serialize(new
        { ProcessId = authenticated.Id, StartedUtcTicks = authenticated.StartTime.ToUniversalTime().Ticks,
          SessionId = authenticated.SessionId, ImagePath = map["modern17"], ContextId = "" }));
        var spoof = Start(duplicate, "spoof", "spoof99");
        Check(await Result(duplicate, "spoof") == "Superseded", "duplicate unauthenticated locator cannot downgrade stronger verified evidence");
        Check(!File.Exists(Path.Combine(duplicate, "trusted.close")), "authenticated fixture remains owner after duplicate forged locator");
        await Stop(spoof, duplicate, "spoof"); await Stop(authenticated, duplicate, "trusted");
    }
    var blocked = Path.Combine(root, "blocked"); Directory.CreateDirectory(blocked);
    using (var held = new FileStream(Path.Combine(blocked, "active.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        var waiting = Start(blocked, "waiting"); Check(await Result(blocked, "waiting") == "Blocked", "unresponsive lease cannot admit another UI owner");
        await Stop(waiting, blocked, "waiting");
    }
    using (var cancellation = new CancellationTokenSource())
    using (var cancelled = new SingleInstanceCoordinator(() => throw new Exception("not active"), Path.Combine(root, "cancel")))
    {
        cancellation.Cancel(); Check(await cancelled.AcquireAsync(cancellation.Token) == InstanceAcquireResult.Superseded, "canceled initialization never acquires");
    }
    var report = Environment.GetEnvironmentVariable("PROCESSKEEPER_INSTANCE_REPORT");
    if (report is not null) File.WriteAllText(report, JsonSerializer.Serialize(new { Checks = results.Count, Failures = 0, Is64BitProcess = Environment.Is64BitProcess, Results = results }));
    Console.WriteLine($"PASS: {results.Count} real owned-process singleton checks; no existing application touched.");
}
finally
{
    foreach (var child in children) File.WriteAllText(Path.Combine(child.Directory, child.Label + ".stop"), "stop");
    foreach (var child in children)
    {
        var watch = Stopwatch.StartNew(); while (!child.Process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(35)) await Task.Delay(25);
        if (!child.Process.HasExited) throw new Exception("Fixture cleanup did not complete: " + child.Label);
        child.Process.Dispose();
    }
}
