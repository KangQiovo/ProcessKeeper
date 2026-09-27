using ProcessKeeper.Core;

internal static class RecoveryHintTests
{
    internal static async Task Run(SpecialWindowSuite suite)
    {
        var self = Process("ProcessKeeper.exe", 900) with { IsSelf = true };
        var app = Process("Telegram.exe", 501);
        WindowRecoveryHint? Hint(ProcessRecord process, params ProcessRecord[] others) =>
            WindowRecoveryHints.ForProcess(process, FakeSpecialWindowHost.Snapshot([self, process, .. others]));

        await suite.Case("recovery hint uses only one verified self identity as current account/session", () =>
        {
            suite.Check(Hint(app)?.Kind == WindowRecoveryKind.TrayApplication, "Supported current-user app receives a candidate badge.");
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(app)) is null, "Missing current process identity cannot establish current user.");
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(self, self with { Id = 901 }, app)) is null, "Two self markers are ambiguous.");
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(self with { OwnerSid = "" }, app)) is null, "Missing self owner cannot establish current user.");
            suite.Check(WindowRecoveryHints.ForProcess(self, FakeSpecialWindowHost.Snapshot(self)) is null, "The application cannot label itself as a recovery candidate.");
        });

        foreach (var invalid in new[]
        {
            app with { OwnerSid = "" }, app with { StartTimeUtcTicks = 0 }, app with { Path = "" },
            app with { Path = "Telegram.exe" }, app with { StartTimeUtcTicks = DateTimeOffset.UtcNow.AddHours(1).UtcTicks },
            app with { Path = @"C:\Fixture Apps\Other.exe" }, app with { Id = 4 },
            app with { OwnerSid = "S-1-5-21-Different" }, app with { SessionId = 2 },
            app with { IsSystem = true }, app with { SessionId = 0 },
            app with { Services = [new("fixture-service", "Fixture service", app.Id)] }
        })
            await suite.Case("invalid or ineligible identity has no recovery badge: " + Describe(invalid), () =>
                suite.Check(Hint(invalid) is null, "Incomplete, foreign, system, or service identity must have no badge."));

        await suite.Case("stale or duplicate snapshot identity has no recovery badge", () =>
        {
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(self)) is null, "A row missing from the current snapshot is rejected.");
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(self, app, app)) is null, "Duplicate PID entries are ambiguous.");
            suite.Check(WindowRecoveryHints.ForProcess(app, FakeSpecialWindowHost.Snapshot(self, app with { StartTimeUtcTicks = app.StartTimeUtcTicks + 1 })) is null, "PID reuse cannot inherit a badge.");
        });

        await suite.Case("hidden main window receives recovery hint before app relaunch hint", () =>
        {
            var hidden = app with { Windows = [FakeSpecialWindowHost.Window() with { IsVisible = false }] };
            var result = Hint(hidden);
            suite.Check(result?.Kind == WindowRecoveryKind.HiddenWindow && result.Text == "可恢复窗口", "Existing hidden main window takes precedence over new launch.");
            suite.Check(result is not null && result.Tooltip.Contains("候选") && result.Tooltip.Contains("重新核实"), "Hint explains that action-time verification is still required.");
        });

        foreach (var badWindow in new[]
        {
            FakeSpecialWindowHost.Window() with { IsVisible = false, IsToolWindow = true },
            FakeSpecialWindowHost.Window() with { IsVisible = false, IsCloaked = true },
            FakeSpecialWindowHost.Window() with { IsVisible = false, ClassName = "tooltips_class32" },
            FakeSpecialWindowHost.Window() with { IsVisible = false, HasUsableBounds = false },
            FakeSpecialWindowHost.Window() with { IsVisible = false, IsTopLevel = false }
        })
            await suite.Case("hidden helper window alone does not imply recoverable UI: " + badWindow, () =>
            {
                var unknown = Process("unknown.exe", 701) with { Windows = [badWindow] };
                suite.Check(Hint(unknown) is null, "Hidden helper geometry and class do not create a main-window hint.");
            });

        await suite.Case("visible and minimized apps use their existing window route", () =>
        {
            suite.Check(Hint(app with { Windows = [FakeSpecialWindowHost.Window()] }) is null, "Visible main UI needs no launch badge.");
            suite.Check(Hint(app with { Windows = [FakeSpecialWindowHost.Window() with { IsMinimized = true }] }) is null, "Minimized UI needs no launch badge.");
        });

        await suite.Case("AVD launcher and linked QEMU engine show explicit restart confirmation", () =>
        {
            var launcher = Process("emulator.exe", 601);
            var engine = Process("qemu-system-x86_64.exe", 602) with { ParentId = launcher.Id, ApplicationKey = "known:avd" };
            suite.Check(Hint(launcher)?.Kind == WindowRecoveryKind.AvdRestart, "SDK launcher is a restart candidate.");
            suite.Check(Hint(launcher)?.Text == "需确认重启", "Restart interruption is exposed directly in badge text.");
            suite.Check(Hint(engine, launcher)?.Kind == WindowRecoveryKind.AvdRestart, "Known AVD core with verified launcher ancestry can show a restart candidate.");
            suite.Check(Hint(engine) is null, "Orphan QEMU core cannot imply a launchable AVD.");
            suite.Check(Hint(engine with { ApplicationKey = "unknown" }, launcher) is null, "Generic QEMU is not assumed to be Android.");
            suite.Check(Hint(engine, launcher with { StartTimeUtcTicks = engine.StartTimeUtcTicks + 1 }) is null, "Reused parent PID cannot prove AVD ancestry.");
            suite.Check(Hint(engine, launcher with { OwnerSid = "S-1-5-21-OTHER" }) is null, "Cross-account parent cannot prove AVD ancestry.");
            suite.Check(Hint(engine, launcher with { SessionId = 2 }) is null, "Cross-session parent cannot prove AVD ancestry.");
        });

        foreach (string name in new[] { "Telegram.exe", "Discord.exe", "chrome.exe", "msedge.exe" })
            await suite.Case("same executable subprocess does not get duplicate launch badge: " + name, () =>
            {
                var parent = Process(name, 601); var child = Process(name, 602) with { ParentId = 601 };
                suite.Check(Hint(child, parent) is null, "A verified same-executable ancestor handles the candidate entry.");
            });

        foreach (string browser in new[] { "chrome.exe", "msedge.exe" })
            await suite.Case("browser candidate badge does not claim proven headless mode: " + browser, () =>
            {
                var hint = Hint(Process(browser, 601));
                suite.Check(hint?.Kind == WindowRecoveryKind.BrowserInspection && hint.Text == "可检查画面", "Only inspection capability is stated.");
                suite.Check(hint is not null && hint.Tooltip.Contains("不表示已确认无头模式"), "A missing window is explicitly distinguished from confirmed headless mode.");
            });

        foreach (string vm in new[] { "VBoxHeadless.exe", "vmware-vmx.exe" })
            await suite.Case("VM engine indicates connect rather than restart: " + vm, () =>
            {
                var hint = Hint(Process(vm, 601));
                suite.Check(hint?.Kind == WindowRecoveryKind.VirtualMachine && hint.Text == "可连接画面", "VM engines advertise a connection candidate.");
                suite.Check(hint is not null && hint.Tooltip.Contains("不会因此发送开机、关机或重启命令"), "VM connection does not imply a power operation.");
            });

        foreach (string sid in new[] { "S-1-5-18", "S-1-5-83-100-200-300" })
            await suite.Case("Hyper-V privileged worker has an explicit account exception: " + sid, () =>
            {
                var vmwp = Process("vmwp.exe", 601) with
                {
                    Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmwp.exe"),
                    SessionId = 0, OwnerSid = sid, IsSystem = true
                };
                suite.Check(Hint(vmwp)?.Kind == WindowRecoveryKind.VirtualMachine, "Verified system-path Hyper-V worker receives only a VM connection candidate.");
                suite.Check(Hint(vmwp with { OwnerSid = FakeSpecialWindowHost.Sid }) is null, "Interactive user account cannot claim the worker exception.");
                suite.Check(Hint(vmwp with { SessionId = 1 }) is null, "Interactive session cannot claim the worker exception.");
                suite.Check(Hint(vmwp with { Path = @"C:\Unknown\vmwp.exe" }) is null, "Non-system worker name receives no privileged hint.");
            });
    }

    private static ProcessRecord Process(string name, int id) => new()
    {
        Id = id, Name = name, Path = @"C:\Fixture Apps\" + name, SessionId = 1,
        OwnerSid = FakeSpecialWindowHost.Sid, StartTimeUtcTicks = FakeSpecialWindowHost.Epoch + id
    };

    private static string Describe(ProcessRecord p) => $"{p.Id}/{p.OwnerSid}/{p.SessionId}/{p.StartTimeUtcTicks}/{p.Path}/system={p.IsSystem}/services={p.Services.Count}";
}
