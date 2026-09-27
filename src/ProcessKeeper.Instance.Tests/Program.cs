using System.Diagnostics;
using System.Reflection;
using ProcessKeeper.App;

if (args is ["--child", var directory, var label])
{
    var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var coordinator = new SingleInstanceCoordinator(() => close.TrySetResult(), directory);
    var result = await coordinator.AcquireAsync(timeout: TimeSpan.FromSeconds(3));
    File.WriteAllText(Path.Combine(directory, label + ".result"), result.ToString());
    if (result == InstanceAcquireResult.Acquired)
    {
        var lifetime = Stopwatch.StartNew();
        while (!close.Task.IsCompleted && !File.Exists(Path.Combine(directory, label + ".stop")) && lifetime.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(30);
        if (close.Task.IsCompleted)
        {
            File.WriteAllText(Path.Combine(directory, label + ".close"), "cooperative");
            await Task.Delay(350); // The lease must remain held while the old UI finishes cancellation.
        }
        coordinator.Dispose();
        File.WriteAllText(Path.Combine(directory, label + ".released"), "released");
    }
    return;
}

var root = Path.Combine(Environment.GetEnvironmentVariable("PROCESSKEEPER_INSTANCE_TEST_ROOT") ?? Path.GetTempPath(),
    "instance-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var children = new List<(Process Process, string Directory, string Label)>();
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
    Console.WriteLine("PASS: " + name);
}
Process Start(string directory, string label)
{
    Directory.CreateDirectory(directory);
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--child"); start.ArgumentList.Add(directory); start.ArgumentList.Add(label);
    var child = Process.Start(start) ?? throw new Exception("Child did not start");
    children.Add((child, directory, label));
    return child;
}
async Task<string> Result(string directory, string label)
{
    var file = Path.Combine(directory, label + ".result");
    var watch = Stopwatch.StartNew();
    while (!File.Exists(file) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
    if (!File.Exists(file)) throw new Exception("Child result timed out: " + label);
    for (var attempt = 0; attempt < 20; attempt++)
    {
        try { var value = File.ReadAllText(file); if (value.Length > 0) return value; }
        catch (IOException) { }
        await Task.Delay(25);
    }
    throw new Exception("Child result empty: " + label);
}
try
{
    var handoff = Path.Combine(root, "handoff");
    Directory.CreateDirectory(handoff);
    File.WriteAllText(Path.Combine(handoff, "broken.json"), "broken");
    File.WriteAllText(Path.Combine(handoff, "stale.json"), "{\"ProcessId\":2147483646,\"StartedUtcTicks\":999999999999999999,\"SessionId\":1,\"ImagePath\":\"C:\\\\Other.exe\"}");
    var first = Start(handoff, "first");
    Check(await Result(handoff, "first") == "Acquired", "first instance acquires despite malformed and stale records");
    var second = Start(handoff, "second");
    Check(await Result(handoff, "second") == "Acquired", "newer instance takes ownership");
    Check(File.Exists(Path.Combine(handoff, "first.close")), "older instance receives cooperative close");
    Check(File.Exists(Path.Combine(handoff, "first.released")), "successor waits until prior cancellation and release");
    await first.WaitForExitAsync();
    Check(first.ExitCode == 0, "old process exits without termination");
    var third = Start(handoff, "third");
    await Task.Delay(60);
    var newest = Start(handoff, "newest");
    Check(await Result(handoff, "newest") == "Acquired", "latest of three overlapping launches wins");
    Check(await Result(handoff, "third") == "Superseded", "intermediate waiting launch never opens management");
    Check(File.Exists(Path.Combine(handoff, "second.close")), "second owner closes when replaced");
    Check(File.Exists(Path.Combine(handoff, "second.released")), "latest owner waits for second release");
    await Task.WhenAll(second.WaitForExitAsync(), third.WaitForExitAsync());
    Check(!newest.HasExited && second.HasExited && third.HasExited, "only the newest fixture remains active");
    var isolated = Path.Combine(root, "another-scope");
    var other = Start(isolated, "other");
    Check(await Result(isolated, "other") == "Acquired" && !newest.HasExited, "separate user-session namespace is independent");
    var blocked = Path.Combine(root, "blocked");
    Directory.CreateDirectory(blocked);
    using (var held = new FileStream(Path.Combine(blocked, "active.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    {
        var waiting = Start(blocked, "waiting");
        Check(await Result(blocked, "waiting") == "Blocked", "unresponsive owner does not permit a second management instance");
        await waiting.WaitForExitAsync();
    }
    using (var cancellation = new CancellationTokenSource())
    using (var cancelled = new SingleInstanceCoordinator(() => throw new Exception("not active"), Path.Combine(root, "cancel")))
    {
        cancellation.Cancel();
        Check(await cancelled.AcquireAsync(cancellation.Token) == InstanceAcquireResult.Superseded, "cancelled launch does not acquire management");
    }
    Console.WriteLine($"PASS: {checks} single-instance checks with real self-owned helper processes; no existing application was touched.");
}
finally
{
    foreach (var child in children) File.WriteAllText(Path.Combine(child.Directory, child.Label + ".stop"), "stop");
    foreach (var child in children)
    {
        await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(18));
        child.Process.Dispose();
    }
}
