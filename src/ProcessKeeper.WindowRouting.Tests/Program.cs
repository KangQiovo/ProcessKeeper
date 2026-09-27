using ProcessKeeper.App;
using ProcessKeeper.Core;
using System.Diagnostics;
using System.Security.Principal;

ProcessKeeper.Core.L.Language = "zh-Hans";

var assertions = 0;
var failures = 0;
void Check(bool condition, string name)
{
    assertions++;
    if (!condition) failures++;
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} | {name}");
}
var started = DateTime.UtcNow.AddMinutes(-5).Ticks;
ProcessRecord FixtureProcess(string name, string key, int id = 999900) => new()
{
    Id = id, Name = name, Path = @"C:\Fixture\" + name, ApplicationKey = key,
    StartTimeUtcTicks = started, SessionId = 1, OwnerSid = "S-1-5-21-1-2-3-1001"
};
WindowRecord Window(string className, bool visible = true) => new((nint)987654, "Android Emulator | Fixture_AVD:5554", visible, false, false, false)
{
    ClassName = className, HasCaption = true, HasUsableBounds = true
};
ApplicationGroup Group(params ProcessRecord[] processes) => new() { Key = "known:avd", Name = "Fixture AVD", Processes = processes };
var self = FixtureProcess("ProcessKeeper.exe", "self", 999901) with { IsSelf = true };

foreach (var className in new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "Notepad" })
    foreach (bool visible in new[] { true, false })
    {
        var process = FixtureProcess("emulator.exe", "known:avd") with { Windows = [Window(className, visible)] };
        var app = Group(process);
        var ui = new MainWindow();
        Check(ui.CandidateCount(app, hiddenOnly: !visible) == 0, $"AVD {className} {(visible ? "visible" : "hidden")} is not a page candidate in actual UI selector");
        await ui.Open(app);
        Check(ui.RestartConfirmationCount == 1 && ui.SuccessNoticeCount == 0 && ui.SelectionDialogCount == 0,
            $"AVD {className} {(visible ? "visible" : "hidden")} routes to restart confirmation, never generic-window success");
        var hint = WindowRecoveryHints.ForProcess(process, new(DateTimeOffset.UtcNow, [self, process], [app]));
        Check(hint?.Kind == WindowRecoveryKind.AvdRestart, $"AVD {className} {(visible ? "visible" : "hidden")} keeps restart badge instead of console recovery");
    }

foreach (bool visible in new[] { true, false })
{
    var process = FixtureProcess("emulator.exe", "known:avd") with { Windows = [Window("Qt5152QWindowIcon", visible)] };
    var ui = new MainWindow();
    Check(ui.CandidateCount(Group(process), hiddenOnly: !visible) == 1, $"real Qt AVD {(visible ? "visible" : "hidden")} remains actionable");
    var cmd = FixtureProcess("cmd.exe", "path:cmd") with { Windows = [Window("ConsoleWindowClass", visible)] };
    Check(ui.CandidateCount(new() { Key = cmd.ApplicationKey, Processes = [cmd] }, hiddenOnly: !visible) == 1,
        $"ordinary command prompt {(visible ? "visible" : "hidden")} retains its own window controls");
}

var minimizedQt = FixtureProcess("emulator.exe", "known:avd") with { Windows = [Window("Qt6QWindowIcon") with { IsMinimized = true }] };
Check(new MainWindow().CandidateCount(Group(minimizedQt)) == 1, "minimized real AVD page retains normal restore path");
foreach (var invalidQt in new[]
{
    Window("Qt6QWindowIcon") with { Title = "Extended controls" },
    Window("Qt6QWindowIcon") with { Title = "" },
    Window("Qt6QWindowIcon") with { IsCloaked = true },
    Window("Qt6QWindowIcon") with { IsToolWindow = true },
    Window("Qt6QWindowIcon") with { HasUsableBounds = false },
    Window("Qt6QWindowIcon") with { HasCaption = false, HasAppWindowStyle = false },
    Window("Qt6QWindowIcon") with { IsTopLevel = false }
})
    Check(new MainWindow().CandidateCount(Group(FixtureProcess("emulator.exe", "known:avd") with { Windows = [invalidQt] })) == 0,
        "AVD Qt helper or unusable window is not a device page: " + invalidQt);

var helper = FixtureProcess("cmd.exe", "path:cmd") with { Windows = [Window("ConsoleWindowClass")] };
Check(new MainWindow().CandidateCount(Group(helper)) == 0, "console grouped under AVD cannot bypass process-name filtering");
var emulatorByName = FixtureProcess("emulator.exe", "path:emulator") with { Windows = [Window("ConsoleWindowClass")] };
Check(new MainWindow().CandidateCount(new() { Key = "path:emulator", Processes = [emulatorByName] }) == 0,
    "standalone emulator launcher still requires a native page without known application key");

var screenshotHelpers = new[]
{
    FixtureProcess("emulator.exe", "known:avd", 47780) with { Windows = [Window("ConsoleWindowClass", false) with { Title = @"E:\Android\Sdk\emulator\emulator.exe" }] },
    FixtureProcess("crashpad_handler.exe", "known:avd", 53028) with { Windows = [Window("ConsoleWindowClass", false) with { Title = @"E:\Android\Sdk\emulator\crashpad_handler.exe" }] },
    FixtureProcess("qemu-system-x86_64.exe", "known:avd", 59872) with { Windows = [Window("Qt6QWindowIcon", false) with { Title = "__wglDummyWindowFodder" }] },
    FixtureProcess("crashpad_handler.exe", "known:avd", 52120) with { Windows = [Window("ConsoleWindowClass", false) with { Title = @"E:\Android\Sdk\emulator\crashpad_handler.exe" }] }
};
var screenshotUi = new MainWindow();
Check(screenshotUi.CandidateCount(Group(screenshotHelpers), hiddenOnly: true) == 0, "all four screenshot console/crashpad/WGL helper rows are excluded");
await screenshotUi.Open(Group(screenshotHelpers));
Check(screenshotUi.RestartConfirmationCount == 1 && screenshotUi.SelectionDialogCount == 0 && screenshotUi.SuccessNoticeCount == 0,
    "screenshot-only helper windows route directly to explicit AVD restart confirmation");
var qtProcess = FixtureProcess("qemu-system-x86_64.exe", "known:avd", 59872) with
{
    Windows =
    [
        Window("Qt6QWindowIcon") with { Handle = 5003, IsMinimized = true },
        Window("Qt6QWindowIcon") with { Handle = 5002 },
        Window("Qt6QWindowIcon") with { Handle = 5001, IsForeground = true },
        Window("ConsoleWindowClass") with { Handle = 5000, IsForeground = true }
    ]
};
var ranked = new MainWindow().Choices(Group([.. screenshotHelpers, qtProcess]));
Check(ranked.Length == 3 && ranked[0].Recommended && ranked[0].RowText.Contains("推荐") &&
    ranked.Skip(1).All(choice => !choice.Recommended && choice.RowText.Contains("AVD画面") && !choice.RowText.Contains("推荐")),
    "only the default eligible Qt page is recommended; remaining eligible pages receive AVD page labels");
Check(ranked.Select(choice => (long)choice.Handle).SequenceEqual(new long[] { 5001, 5002, 5003 }),
    "multiple Qt pages default to foreground, then normal, then minimized page with deterministic order");
var ordinaryRows = new MainWindow().Choices(new() { Key = "path:cmd", Processes = [helper] });
Check(ordinaryRows.Length == 1 && !ordinaryRows[0].Recommended && !ordinaryRows[0].RowText.Contains("推荐"),
    "ordinary command prompt is operable but never labeled as recommended AVD page");
var singleQtUi = new MainWindow();
await singleQtUi.Open(Group(qtProcess with { Windows = [qtProcess.Windows[1]] }));
Check(singleQtUi.SelectionDialogCount == 0 && singleQtUi.RestartConfirmationCount == 0,
    "a single visible Qt candidate does not gain an unnecessary selection or restart dialog");

using var current = System.Diagnostics.Process.GetCurrentProcess();
var ownIdentity = new ProcessRecord
{
    Id = current.Id, Name = Path.GetFileName(Environment.ProcessPath!), Path = Environment.ProcessPath!,
    SessionId = current.SessionId, StartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks,
    OwnerSid = WindowsIdentity.GetCurrent().User?.Value ?? ""
};
foreach (var className in new[] { "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow" })
{
    using var windows = new OwnWindows(className);
    var hidden = windows.Snapshot("main");
    Check(hidden.ClassName == className && !hidden.IsVisible, "fixture owns actual hidden Win32 " + className);
    // The stale snapshot claims Qt eligibility; action-time native inspection must refuse its real class.
    var staleQt = hidden with { ClassName = "Qt6QWindowIcon" };
    var blocked = WindowActions.RevealHidden(ownIdentity, staleQt, "known:avd");
    Check(!blocked.Success && !Native.IsWindowVisible(hidden.Handle), "live recheck rejects forged Qt metadata for " + className);
    var ordinary = WindowActions.RevealHidden(ownIdentity, hidden);
    Check(ordinary.Success && Native.IsWindowVisible(hidden.Handle), "ordinary console-window owner can still reveal " + className);
    var visible = windows.Snapshot("main");
    blocked = WindowActions.Activate(ownIdentity, visible with { ClassName = "Qt6QWindowIcon" }, "known:avd");
    Check(!blocked.Success, "live recheck rejects visible console activation as AVD success for " + className);
    var minimized = WindowActions.Minimize(ownIdentity, visible);
    Check(minimized.Success && Native.IsIconic(hidden.Handle), "ordinary console-window owner can still minimize " + className);
}
using (var windows = new OwnWindows("Qt6QWindowRoutingFixture"))
{
    var hidden = windows.Snapshot("main");
    var restored = WindowActions.RevealHidden(ownIdentity, hidden, "known:avd");
    Check(restored.Success && Native.IsWindowVisible(hidden.Handle), "actual owned Qt main-window shape passes native AVD reveal checks");
    var minimized = WindowActions.Minimize(ownIdentity, windows.Snapshot("main"), "known:avd");
    Check(minimized.Success && Native.IsIconic(hidden.Handle), "actual owned Qt main-window shape retains minimize support");
    var stale = windows.Snapshot("main");
    var changed = Native.SetWindowText(hidden.Handle, "Extended controls");
    Check(changed && !WindowActions.Activate(ownIdentity, stale, "known:avd").Success && Native.IsIconic(hidden.Handle),
        "live title change to helper page prevents restore despite stale eligible snapshot");
}
Console.WriteLine($"RESULT | assertions={assertions} | failed={failures}");
return failures == 0 ? 0 : 1;
