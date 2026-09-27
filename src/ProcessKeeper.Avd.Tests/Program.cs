using System.Diagnostics;
using System.Text.Json;
using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

var suite = new FixtureSuite();
var timing = new AvdRestartTiming(TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(260), TimeSpan.FromMilliseconds(20), 3);

await suite.Case("native GUI matcher accepts the emulator's Qt main window", () =>
{
    suite.Check(AvdGuiMatcher.IsGuiWindow(FakeAvdHost.GuiWindow(), FakeAvdHost.AvdName, FakeAvdHost.Port), "valid Qt main window accepted");
    var onlyPort = FakeAvdHost.GuiWindow() with { Title = "Android Emulator - port 5554" };
    suite.Check(AvdGuiMatcher.IsGuiWindow(onlyPort, FakeAvdHost.AvdName, FakeAvdHost.Port), "verified Android Emulator port title accepted");
    return Task.CompletedTask;
});

var windowCases = new Dictionary<string, WindowRecord>
{
    ["cmd console"] = FakeAvdHost.GuiWindow() with { ClassName = "ConsoleWindowClass" },
    ["Windows Terminal"] = FakeAvdHost.GuiWindow() with { ClassName = "CASCADIA_HOSTING_WINDOW_CLASS" },
    ["generic titled window"] = FakeAvdHost.GuiWindow() with { ClassName = "Notepad" },
    ["zero handle"] = FakeAvdHost.GuiWindow() with { Handle = 0 },
    ["hidden window"] = FakeAvdHost.GuiWindow() with { IsVisible = false },
    ["minimized window"] = FakeAvdHost.GuiWindow() with { IsMinimized = true },
    ["cloaked window"] = FakeAvdHost.GuiWindow() with { IsCloaked = true },
    ["tool window"] = FakeAvdHost.GuiWindow() with { IsToolWindow = true },
    ["child window"] = FakeAvdHost.GuiWindow() with { IsTopLevel = false },
    ["zero region"] = FakeAvdHost.GuiWindow() with { HasUsableBounds = false },
    ["no main-window style"] = FakeAvdHost.GuiWindow() with { HasCaption = false, HasAppWindowStyle = false },
    ["another device title"] = FakeAvdHost.GuiWindow() with { Title = "Android Emulator | Different_Device:5556" },
    ["port number substring"] = FakeAvdHost.GuiWindow() with { Title = "Android Emulator | port 15554" }
};
foreach (var (label, window) in windowCases)
    await suite.Case("native GUI matcher rejects " + label, () =>
    {
        suite.Check(!AvdGuiMatcher.IsGuiWindow(window, FakeAvdHost.AvdName, FakeAvdHost.Port), label + " is not native GUI success");
        return Task.CompletedTask;
    });

await suite.Case("discovery is read-only and prepares a standalone GUI launch", async () =>
{
    var host = new FakeAvdHost();
    host.SourceArguments = [FakeAvdHost.LauncherPath, "-avd", FakeAvdHost.AvdName, "-port", "5554",
        "-qt-hide-window", "-grpc-use-token", "-idle-grpc-timeout", "300", "-no-window"];
    var plan = await Plan(host);
    suite.Check(host.ShutdownCount == 0 && host.LaunchCount == 0, "discover/confirmation preview causes no shutdown or launch");
    suite.Check(plan.AvdName == FakeAvdHost.AvdName && plan.ConsolePort == FakeAvdHost.Port, "plan binds exact AVD and console port");
    suite.Check(FakeAvdHost.IdentityEquals(plan.Launcher, host.OldLauncher) && FakeAvdHost.IdentityEquals(plan.Engine, host.OldEngine), "plan binds both old process identities");
    suite.Check(!plan.LaunchArguments.Any(value => value is "-no-window" or "-qt-hide-window" or "-grpc-use-token" or "-idle-grpc-timeout" or "300"), "headless and embedded lifecycle options removed");
    suite.Check(plan.LaunchArguments.Contains("-no-snapshot-load") && plan.GuiEnginePath == FakeAvdHost.GuiPath, "cold GUI plan uses non-headless engine path");
    suite.Check(plan.LauncherSha256 == FakeAvdHost.Hash, "plan pins executable hash");
});

await suite.Case("already cancelled execution causes zero side effects", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host);
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { var result = await Service(host).ExecuteAsync(plan, cancellationToken: cancelled.Token); suite.Check(!result.Success, "cancelled result cannot be successful"); }
    catch (OperationCanceledException) { suite.Check(true, "cancellation returned explicitly"); }
    suite.Check(host.ShutdownCount == 0 && host.LaunchCount == 0, "cancel before execution neither stops nor starts a process");
});

await RejectDiscovery("ambiguous direct engine children", host => host.AdditionalOldProcesses = [host.OldEngine with { Id = 12003 }]);
await RejectDiscovery("engine belongs to another SDK", host => host.OldEngine = host.OldEngine with { Path = @"C:\OtherSdk\qemu\windows-x86_64\qemu-system-x86_64-headless.exe" });
await RejectDiscovery("engine belongs to another session", host => host.OldEngine = host.OldEngine with { SessionId = 2 });
await RejectDiscovery("engine belongs to another user", host => host.OldEngine = host.OldEngine with { OwnerSid = "S-1-5-21-111-222-333-1002" });
await RejectDiscovery("engine creation predates launcher", host => host.OldEngine = host.OldEngine with { StartTimeUtcTicks = host.OldLauncher.StartTimeUtcTicks - 1 });
await RejectDiscovery("old console AVD differs from launch arguments", host => host.OldAvdName = "Other_AVD");
await RejectDiscovery("console listener missing", host => host.OmitListener = true);
await RejectDiscovery("console listener has conflicting owners", host => host.SecondListenerOwner = true);
await RejectDiscovery("USERPROFILE unavailable", host => host.Environment = new Dictionary<string, string>());
await RejectDiscovery("GUI counterpart does not exist", host => host.MissingFiles.Add(FakeAvdHost.GuiPath));
await RejectDiscovery("unknown original launch flag", host => host.SourceArguments = [FakeAvdHost.LauncherPath, "-avd", FakeAvdHost.AvdName, "-unrecognized-fixture-flag"]);

foreach (var key in new[] { "ANDROID_AVD_HOME", "HOME", "TEMP" })
{
    await RejectDiscovery("relative environment path " + key, host =>
        host.Environment = new Dictionary<string, string>(host.Environment, StringComparer.OrdinalIgnoreCase) { [key] = @"relative\directory" });

    await suite.Case("execution rejects matching relative environment path " + key, async () =>
    {
        var host = new FakeAvdHost(); var plan = await Plan(host);
        var environment = new Dictionary<string, string>(host.Environment, StringComparer.OrdinalIgnoreCase) { [key] = @"relative\directory" };
        host.Environment = environment;
        // Both sides match: rejection must validate path semantics, not merely detect environment changes.
        var result = await Service(host).ExecuteAsync(plan with { Environment = environment });
        suite.Check(!result.Success && host.ShutdownCount == 0 && host.LaunchCount == 0, "relative paths are rejected before any shutdown even when plan and current environment agree");
    });

    foreach (var value in new[] { @"D:\FixtureDirectories\" + key, "" })
        await suite.Case("environment preserves " + (value.Length == 0 ? "empty " : "absolute ") + key, async () =>
        {
            var host = new FakeAvdHost();
            host.Environment = new Dictionary<string, string>(host.Environment, StringComparer.OrdinalIgnoreCase) { [key] = value };
            var plan = await Plan(host);
            suite.Check(plan.Environment.TryGetValue(key, out var planned) && planned == value, "discovery preserves the original absolute or empty environment value");
            var result = await Service(host).ExecuteAsync(plan);
            suite.Check(result.Success && host.ShutdownCount == 1 && host.LaunchCount == 1, "supported environment values remain executable");
            suite.Check(host.LastLaunchedPlan?.Environment.TryGetValue(key, out var launchedValue) == true && launchedValue == value, "replacement plan retains the same environment value");
        });
}

await suite.Case("discovery never selects an unrequested launcher", async () =>
{
    var host = new FakeAvdHost();
    var result = await Service(host).DiscoverAsync([host.OldEngine]);
    suite.Check(result.Plans.Count == 0 && host.ShutdownCount == 0 && host.LaunchCount == 0, "an orphan engine selection cannot implicitly target its launcher");
});

await suite.Case("discovery binds a verified port when the original launch used automatic allocation", async () =>
{
    var host = new FakeAvdHost { SourceArguments = [FakeAvdHost.LauncherPath, "-avd", FakeAvdHost.AvdName, "-no-window"] };
    var plan = await Plan(host);
    suite.Check(plan.ConsolePort == FakeAvdHost.Port && plan.LaunchArguments.TakeLast(2).SequenceEqual(["-port", "5554"]), "verified port is carried explicitly into replacement launch");
});

await RejectBeforeStop("old engine creation time changed", host => host.OldEngine = host.OldEngine with { StartTimeUtcTicks = host.OldEngine.StartTimeUtcTicks + 1 });
await RejectBeforeStop("old launcher creation time changed", host => host.OldLauncher = host.OldLauncher with { StartTimeUtcTicks = host.OldLauncher.StartTimeUtcTicks + 1 });
await RejectBeforeStop("old engine executable path changed", host => host.OldEngine = host.OldEngine with { Path = @"C:\AnotherSdk\qemu-system-x86_64-headless.exe" });
await RejectBeforeStop("old console AVD name changed", host => host.OldAvdName = "Other_AVD");
await RejectBeforeStop("console port owner changed", host => host.ListenerOwnerOverride = 32001);
await RejectBeforeStop("console port acquired a second owner", host => host.SecondListenerOwner = true);
await RejectBeforeStop("emulator executable hash changed", host => host.CurrentHash = new string('B', 64));
await RejectBeforeStop("GUI engine disappeared", host => host.MissingFiles.Add(FakeAvdHost.GuiPath));
await RejectBeforeStop("old launch arguments changed", host => host.SourceArguments = [FakeAvdHost.LauncherPath, "-avd", "Other_AVD", "-port", "5554"]);
await RejectBeforeStop("old environment changed", host => host.Environment = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\OtherProfile" });

foreach (var exit in new[] { OldExitBehavior.NeverExit, OldExitBehavior.EngineOnly, OldExitBehavior.LauncherOnly })
    await suite.Case("old shutdown incomplete: " + exit, async () =>
    {
        var host = new FakeAvdHost { ExitBehavior = exit }; var plan = await Plan(host);
        var result = await Service(host).ExecuteAsync(plan);
        suite.Check(!result.Success && host.ShutdownCount == 1 && host.LaunchCount == 0, "old process still alive prevents every new launch");
        suite.Check(result.ShutdownRequested && !result.LaunchStarted, "result identifies the completed and blocked stages");
    });

await suite.Case("graceful shutdown refusal never falls back to forced termination or launch", async () =>
{
    var host = new FakeAvdHost { RefuseShutdown = true }; var plan = await Plan(host);
    var result = await Service(host).ExecuteAsync(plan);
    suite.Check(!result.Success && host.ShutdownCount == 1 && host.LaunchCount == 0, "refused graceful shutdown does not launch a second instance");
});

await suite.Case("retained console port blocks launch after old processes exit", async () =>
{
    var host = new FakeAvdHost { RetainPortAfterExit = true }; var plan = await Plan(host);
    var result = await Service(host).ExecuteAsync(plan);
    suite.Check(!result.Success && host.LaunchCount == 0, "different listener after shutdown prevents a conflicting launch");
});

await suite.Case("non-adjacent configured ADB port must be free before restart", async () =>
{
    var host = new FakeAvdHost
    {
        SourceArguments = [FakeAvdHost.LauncherPath, "-avd", FakeAvdHost.AvdName, "-ports", "5554,5561", "-no-window"],
        AdditionalListenersAfterExit = [new(5561, 32001, false) { Address = "127.0.0.1" }]
    };
    var plan = await Plan(host);
    var result = await Service(host).ExecuteAsync(plan);
    suite.Check(!result.Success && host.ShutdownCount == 1 && host.LaunchCount == 0, "the explicitly configured ADB listener prevents launch even when console+1 is free");
});

await suite.Case("SDK mutation during old shutdown prevents replacement launch", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host);
    host.OnShutdown = () => host.CurrentHash = new string('B', 64);
    var result = await Service(host).ExecuteAsync(plan);
    suite.Check(!result.Success && result.ShutdownRequested && host.LaunchCount == 0, "post-exit executable hash check prevents launching changed content");
});

await suite.Case("concurrent execution is rejected without a second shutdown or launch", async () =>
{
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var host = new FakeAvdHost { ShutdownGate = gate.Task }; var plan = await Plan(host); var service = Service(host);
    var first = service.ExecuteAsync(plan);
    var second = await service.ExecuteAsync(plan);
    suite.Check(!second.Success && host.ShutdownCount == 1 && host.LaunchCount == 0, "in-flight operation prevents duplicate confirmation execution");
    gate.SetResult();
    var completed = await first;
    suite.Check(completed.Success && host.ShutdownCount == 1 && host.LaunchCount == 1, "first operation remains able to complete once its own shutdown finishes");
});

await suite.Case("cancel after graceful stop never starts a replacement", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host);
    using var cancellation = new CancellationTokenSource(); host.OnShutdown = cancellation.Cancel;
    try
    {
        var result = await Service(host).ExecuteAsync(plan, cancellationToken: cancellation.Token);
        suite.Check(!result.Success && result.ShutdownRequested && !result.LaunchStarted, "cancelled result preserves that shutdown was requested");
    }
    catch (OperationCanceledException) { suite.Check(true, "post-stop cancellation surfaced"); }
    suite.Check(host.ShutdownCount == 1 && host.LaunchCount == 0, "post-stop cancellation does not launch");
});

await suite.Case("native GUI succeeds only after stable observations of the same identity", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host);
    var result = await Service(host).ExecuteAsync(plan);
    suite.Check(result.Success && result.ShutdownRequested && result.LaunchStarted, "verified GUI workflow succeeds");
    suite.Check(host.CapturesAfterLaunch >= 3, "at least three fresh process/window observations taken");
    suite.Check(host.CaptureTimes[^1] - host.CaptureTimes[0] >= TimeSpan.FromMilliseconds(30), "stability samples are separated in time");
    suite.Check(result.VisibleProcess?.Id == host.NewEngine.Id && result.VisibleWindow?.Handle == 4001, "result identifies actual new GUI owner and HWND");
    suite.Check(host.NameReadCount >= 3, "AVD console identity rechecked after launch");
    suite.Check(host.Operations.IndexOf("shutdown") < host.Operations.IndexOf("launch"), "launch occurs after the confirmed shutdown request");
    suite.Check(host.DisposeCount == 1, "held launcher identity disposed exactly once after verification");
});

foreach (var label in new[] { "cmd console", "Windows Terminal", "hidden window", "cloaked window", "tool window", "zero region", "child window" })
    await RejectAfterLaunch("new process has only " + label, host => host.NewEngine = host.NewEngine with { Windows = [windowCases[label]] });

await RejectAfterLaunch("new process has no window", host => host.NewEngine = host.NewEngine with { Windows = [] });
await RejectAfterLaunch("new console reports another AVD", host => host.NewAvdName = "Other_AVD");
await RejectAfterLaunch("GUI belongs to an unrelated existing process", host => host.NewEngine = host.NewEngine with { ParentId = 31001 });
await RejectAfterLaunch("GUI owner predates the newly started launcher", host => host.NewEngine = host.NewEngine with { StartTimeUtcTicks = host.NewLauncher.StartTimeUtcTicks - 1 });
await RejectAfterLaunch("GUI owner belongs to another SDK", host => host.NewEngine = host.NewEngine with { Path = @"C:\AnotherSdk\qemu-system-x86_64.exe" });
await RejectAfterLaunch("GUI owner still uses the headless engine", host => host.NewEngine = host.NewEngine with { Path = FakeAvdHost.EnginePath, Name = "qemu-system-x86_64-headless.exe" });
await RejectAfterLaunch("GUI owner commandline identifies another AVD", host => host.ArgumentOverrides[host.NewEngine.Id] = [FakeAvdHost.GuiPath, "-avd", "Other_AVD", "-port", "5554"]);
await RejectAfterLaunch("GUI owner commandline identifies another port", host => host.ArgumentOverrides[host.NewEngine.Id] = [FakeAvdHost.GuiPath, "-avd", FakeAvdHost.AvdName, "-port", "5556"]);
await RejectAfterLaunch("child starts after the held launcher exit time", host =>
{
    host.StartedRootHasExited = true;
    host.StartedRootExitTime = host.NewLauncher.StartTimeUtcTicks;
});
await RejectAfterLaunch("exited launcher has no verifiable exit time", host => host.StartedRootHasExited = true);
await RejectAfterLaunch("transient GUI disappears after one sample", host => host.NewCapture = index =>
    index == 1 ? [host.NewLauncher, host.NewEngine] : [host.NewLauncher, host.NewEngine with { Windows = [] }]);
await RejectAfterLaunch("HWND changes every observation", host => host.NewCapture = index =>
    [host.NewLauncher, host.NewEngine with { Windows = [FakeAvdHost.GuiWindow(4000 + index)] }]);
await RejectAfterLaunch("same numeric PID and HWND are reused with changing creation identity", host =>
    host.OnCaptureAfterLaunch = _ => host.NewEngine = host.NewEngine with { StartTimeUtcTicks = host.NewEngine.StartTimeUtcTicks + 1 });

await suite.Case("cancel while waiting for GUI retains the started process and releases its handle", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host);
    using var cancellation = new CancellationTokenSource(); host.OnLaunch = cancellation.Cancel;
    var result = await Service(host).ExecuteAsync(plan, cancellationToken: cancellation.Token);
    suite.Check(!result.Success && result.ShutdownRequested && result.LaunchStarted, "post-launch cancellation reports the actual side effects");
    suite.Check(host.LaunchCount == 1 && host.DisposeCount == 1, "cancel releases handle without launching a replacement again");
});

await suite.Case("a delayed stable native window can succeed", async () =>
{
    var host = new FakeAvdHost();
    host.NewCapture = index => [host.NewLauncher, index <= 2 ? host.NewEngine with { Windows = [] } : host.NewEngine];
    var result = await Service(host).ExecuteAsync(await Plan(host));
    suite.Check(result.Success && host.CapturesAfterLaunch >= 5, "missing early window is retried until three stable GUI samples");
});

await suite.Case("GUI child created within the exited launcher's lifetime is accepted", async () =>
{
    var host = new FakeAvdHost { StartedRootHasExited = true };
    host.StartedRootExitTime = host.NewEngine.StartTimeUtcTicks + 1;
    var result = await Service(host).ExecuteAsync(await Plan(host));
    suite.Check(result.Success, "held root exit timestamp permits a genuine already-created child");
});

await suite.Case("launch failure is reported without false window success", async () =>
{
    var host = new FakeAvdHost { FailLaunch = true }; var result = await Service(host).ExecuteAsync(await Plan(host));
    suite.Check(!result.Success && host.ShutdownCount == 1 && host.LaunchCount == 1, "launch exception cannot become GUI success");
});

await suite.Case("same plan cannot restart a replacement instance a second time", async () =>
{
    var host = new FakeAvdHost(); var plan = await Plan(host); var service = Service(host);
    var first = await service.ExecuteAsync(plan); var second = await service.ExecuteAsync(plan);
    suite.Check(first.Success && !second.Success && host.ShutdownCount == 1 && host.LaunchCount == 1, "old identity binding makes stale plan re-execution side-effect free");
});

await NativeBoundaryTests.Run(suite);

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "fixture-result.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
{
    Fixture = "AVD restart | synthetic workflow and exclusively owned native boundaries",
    suite.PassedCases, suite.FailedCases, suite.Assertions, Skipped = 0,
    RealAvdTouched = false, Failures = suite.Failures
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"AVD fixture | {suite.PassedCases} cases passed | {suite.FailedCases} failed | {suite.Assertions} assertions | 0 skipped");
return suite.FailedCases == 0 ? 0 : 1;

AvdRestartService Service(FakeAvdHost host) => new(host, timing);
async Task<AvdRestartPlan> Plan(FakeAvdHost host)
{
    var discovery = await Service(host).DiscoverAsync(host.Capture().Processes);
    if (discovery.Plans.Count != 1) throw new InvalidOperationException("Fixture expected one discovered plan: " + string.Join(" | ", discovery.Notices));
    return discovery.Plans[0];
}
async Task RejectBeforeStop(string label, Action<FakeAvdHost> change)
{
    await suite.Case(label, async () =>
    {
        var host = new FakeAvdHost(); var plan = await Plan(host); change(host);
        var result = await Service(host).ExecuteAsync(plan);
        suite.Check(!result.Success && host.ShutdownCount == 0 && host.LaunchCount == 0, "changed precondition causes zero shutdowns and launches");
    });
}
async Task RejectDiscovery(string label, Action<FakeAvdHost> change)
{
    await suite.Case("discovery rejects " + label, async () =>
    {
        var host = new FakeAvdHost(); change(host);
        var result = await Service(host).DiscoverAsync(host.Capture().Processes);
        suite.Check(result.Plans.Count == 0 && result.Notices.Count > 0, "unsupported or ambiguous identity never produces an executable restart plan");
        suite.Check(host.ShutdownCount == 0 && host.LaunchCount == 0, "failed discovery has no process side effects");
    });
}
async Task RejectAfterLaunch(string label, Action<FakeAvdHost> change)
{
    await suite.Case(label, async () =>
    {
        var host = new FakeAvdHost(); var plan = await Plan(host); change(host);
        var result = await Service(host).ExecuteAsync(plan);
        suite.Check(!result.Success && host.ShutdownCount == 1 && host.LaunchCount == 1, "successful process creation without verified GUI remains failure");
        suite.Check(host.DisposeCount == 1, "held launcher identity released on failed verification");
    });
}

internal sealed class FixtureSuite
{
    internal int PassedCases { get; private set; }
    internal int FailedCases { get; private set; }
    internal int Assertions { get; private set; }
    internal List<string> Failures { get; } = [];
    internal void Check(bool condition, string message)
    {
        Assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
    internal async Task Case(string label, Func<Task> test)
    {
        try { await test(); PassedCases++; Console.WriteLine("PASS | " + label); }
        catch (Exception error) { FailedCases++; Failures.Add(label + " | " + error); Console.WriteLine("FAIL | " + label + " | " + error.Message); }
    }
}
