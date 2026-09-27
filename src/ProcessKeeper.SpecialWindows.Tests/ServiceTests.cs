using ProcessKeeper.Core;

internal static class ServiceTests
{
    private static readonly SpecialWindowTiming Fast = new(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(1));
    private static SpecialWindowService Service(FakeSpecialWindowHost host) => new(host, Fast);

    internal static async Task Run(SpecialWindowSuite suite)
    {
        await suite.Case("discovery considers only still-identical selected sources and does not launch", async () =>
        {
            var host = new FakeSpecialWindowHost(); var source = host.Add("Telegram.exe");
            var unselected = host.Add("Discord.exe", id: 502);
            var found = await Service(host).DiscoverAsync([source, source with { Id = 700 }, source with { StartTimeUtcTicks = source.StartTimeUtcTicks + 1 }]);
            suite.Check(found.Plans.Count == 1 && found.Plans[0].Source == source, "Only the selected live source is eligible.");
            suite.Check(host.Launches.Count == 0, "Discovery performs no launch.");
        });

        await suite.Case("discovery isolates inaccessible applications as notices", async () =>
        {
            var host = new FakeSpecialWindowHost(); var broken = host.Add("Telegram.exe"); var good = host.Add("Discord.exe", id: 502);
            host.OnValidate = (p, _) => { if (p.Id == broken.Id) throw new InvalidOperationException("Fixture access denied"); };
            var found = await Service(host).DiscoverAsync([broken, good]);
            suite.Check(found.Plans.Count == 1 && found.Plans[0].Source == good, "One inaccessible source does not remove other verified choices.");
            suite.Check(found.Notices.Count == 1 && found.Notices[0].Contains(broken.Id.ToString()), "The failed source receives a specific notice.");
        });

        await suite.Case("discovery deduplicates shared app entry without combining different VMs", async () =>
        {
            var host = new FakeSpecialWindowHost();
            var first = host.Add("Telegram.exe"); var second = host.Add("Telegram.exe", first.Path, 502);
            var found = await Service(host).DiscoverAsync([first, second]);
            suite.Check(found.Plans.Count == 1, "Two instances sharing a GUI entry provide one application choice.");
            var (vmHost, vm) = PolicyTests.VBox();
            var other = vmHost.Add(vm.Name, vm.Path, 502, "--startvm", FakeSpecialWindowHost.OtherVmId);
            vmHost.OnReadVm = (p, _, target) => new(target, p.Id == vm.Id ? "Alpha" : "Beta", p.Id);
            var vms = await Service(vmHost).DiscoverAsync([vm, other]);
            suite.Check(vms.Plans.Count == 2 && vms.Plans.Select(p => p.TargetId).Distinct().Count() == 2, "Different VM IDs remain individually selectable.");
        });

        await suite.Case("no confirmation means no validation, capture, or launch", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            var result = await Service(host).ExecuteAsync(plan, confirmed: false);
            suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "Declined launch performs no action.");
            suite.Check(host.Validations == 0 && host.Captures == 0, "Declined plan is not processed further.");
        });

        await suite.Case("pre-cancelled execution never launches", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var result = await Service(host).ExecuteAsync(plan, true, cancellationToken: cancellation.Token);
            suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "Cancellation before execution leaves source untouched.");
        });

        await suite.Case("source PID reuse before execution never launches", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.Processes[0] = source with { StartTimeUtcTicks = source.StartTimeUtcTicks + 1 };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "A recycled PID cannot authorize the old plan.");
        });

        await suite.Case("source changed while taking prelaunch snapshot never launches", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.OnCapture = () =>
            {
                host.Processes[0] = source with { StartTimeUtcTicks = source.StartTimeUtcTicks + 1 };
                return FakeSpecialWindowHost.Snapshot(host.Processes.ToArray());
            };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "Source must still be alive and identical immediately before launch.");
        });

        foreach (string change in new[] { "source arguments", "frontend hash", "VM target", "VM name", "frontend missing", "frontend console" })
            await suite.Case("changed plan inputs cannot launch: " + change, async () =>
            {
                var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
                switch (change)
                {
                    case "source arguments": host.Arguments[source.Id] = [source.Path, "--startvm", FakeSpecialWindowHost.OtherVmId]; break;
                    case "frontend hash": host.Hashes[plan.ExecutablePath] = "replacement-content"; break;
                    case "VM target": host.OnReadVm = (p, _, _) => new(FakeSpecialWindowHost.OtherVmId, "Alpha", p.Id); break;
                    case "VM name": host.OnReadVm = (p, _, _) => new(FakeSpecialWindowHost.VmId, "Renamed", p.Id); break;
                    case "frontend missing": host.Files.Remove(plan.ExecutablePath); break;
                    case "frontend console": host.GuiFiles.Remove(plan.ExecutablePath); break;
                }
                var result = await Service(host).ExecuteAsync(plan, true);
                suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "Revalidation stops before launch when " + change + " changed.");
            });

        await suite.Case("tampering with prepared executable and args never launches", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            foreach (var changed in new[]
            {
                plan with { ExecutablePath = @"C:\Windows\System32\cmd.exe" },
                plan with { Arguments = ["--startvm", FakeSpecialWindowHost.OtherVmId, "--separate"] },
                plan with { Kind = SpecialWindowKind.TrayApplication }
            })
            {
                var result = await Service(host).ExecuteAsync(changed, true);
                suite.Check(!result.Success && !result.LaunchRequested && host.Launches.Count == 0, "Only a freshly reconstructed plan can execute.");
            }
        });

        await suite.Case("timeout sends one GUI request without retry or success from process creation", async () =>
        {
            var (host, source) = PolicyTests.VBox();
            var result = await Service(host).ExecuteAsync(host.Prepare(source), true);
            suite.Check(!result.Success && result.LaunchRequested && host.Launches.Count == 1, "Missing GUI times out with exactly one request.");
            suite.Check(result.Process is null && result.Window is null, "No fabricated window evidence on timeout.");
        });

        await suite.Case("cancellation after launch preserves the launched request without retry", async () =>
        {
            var (host, source) = PolicyTests.VBox(); using var cancellation = new CancellationTokenSource();
            host.OnLaunch = _ => cancellation.Cancel();
            var result = await Service(host).ExecuteAsync(host.Prepare(source), true, cancellationToken: cancellation.Token);
            suite.Check(!result.Success && result.LaunchRequested && host.Launches.Count == 1, "After-launch cancellation is reported accurately and cannot retry.");
            suite.Check(host.Processes.Contains(source), "Fixture source is retained.");
        });

        await suite.Case("concurrent execution is rejected while the first window request is pending", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source); var service = Service(host);
            var first = service.ExecuteAsync(plan, true);
            var second = await service.ExecuteAsync(plan, true);
            await first;
            suite.Check(!second.Success && !second.LaunchRequested && host.Launches.Count == 1, "The same service cannot send overlapping requests.");
        });

        foreach (SpecialWindowKind kind in Enum.GetValues<SpecialWindowKind>())
            await suite.Case("verified GUI succeeds only after stable samples: " + kind, async () =>
            {
                var (host, source) = Fixture(kind); var plan = host.Prepare(source);
                ProcessRecord? frontend = null;
                host.OnLaunch = _ => frontend = AddFrontend(host, plan);
                var result = await Service(host).ExecuteAsync(plan, true);
                suite.Check(result.Success && result.LaunchRequested && host.Launches.Count == 1, "A valid frontend should be reported as successful.");
                suite.Check(result.Process?.Id == frontend?.Id && result.Window?.Handle == 101, "Success returns the verified process and HWND.");
                suite.Check(host.Captures >= 5, "Before, three stable observations, and final fresh capture are required.");
            });

        var proper = FakeSpecialWindowHost.Window("Alpha - Oracle VirtualBox") with { ClassName = "Qt5152QWindowIcon" };
        var invalid = new (string Name, WindowRecord Window)[]
        {
            ("console", proper with { ClassName = "ConsoleWindowClass" }),
            ("terminal", proper with { ClassName = "CASCADIA_HOSTING_WINDOW_CLASS" }),
            ("tool", proper with { IsToolWindow = true }), ("minimized", proper with { IsMinimized = true }),
            ("cloaked", proper with { IsCloaked = true }), ("hidden", proper with { IsVisible = false }),
            ("blank", proper with { Title = " " }), ("wrong VM title", proper with { Title = "Beta - Oracle VirtualBox" }),
            ("manager home", proper with { Title = "Oracle VirtualBox Manager" }),
            ("wrong UI class", proper with { ClassName = "FixtureMainWindow" })
        };
        foreach (var sample in invalid)
            await suite.Case("observed window cannot establish success: " + sample.Name, async () =>
            {
                var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
                host.OnLaunch = _ => AddFrontend(host, plan, sample.Window);
                var result = await Service(host).ExecuteAsync(plan, true);
                suite.Check(!result.Success && result.LaunchRequested && host.Launches.Count == 1, "Invalid GUI evidence must time out without retry.");
            });

        await suite.Case("a shell window cannot substitute for the expected frontend", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.OnLaunch = _ =>
            {
                var shell = host.Add("cmd.exe", @"C:\Windows\System32\cmd.exe", 502);
                host.Processes[^1] = shell with { Windows = [proper] };
            };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success && host.Launches.Count == 1, "Correct-looking title on the wrong executable cannot count.");
        });

        foreach (string mismatch in new[] { "UUID", "argv path", "owner", "session", "renderer" })
            await suite.Case("frontend identity mismatch is rejected: " + mismatch, async () =>
            {
                var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
                host.OnLaunch = _ =>
                {
                    var frontend = AddFrontend(host, plan);
                    if (mismatch == "UUID") host.Arguments[frontend.Id] = [frontend.Path, "--startvm", FakeSpecialWindowHost.OtherVmId, "--separate"];
                    if (mismatch == "argv path") host.Arguments[frontend.Id] = [@"C:\Impostor\VirtualBoxVM.exe", .. plan.Arguments];
                    if (mismatch == "renderer") host.Arguments[frontend.Id] = [frontend.Path, .. plan.Arguments, "--type=renderer"];
                    if (mismatch == "owner") host.Processes[^1] = frontend with { OwnerSid = "S-1-5-21-OTHER" };
                    if (mismatch == "session") host.Processes[^1] = frontend with { SessionId = source.SessionId + 1 };
                };
                var result = await Service(host).ExecuteAsync(plan, true);
                suite.Check(!result.Success && host.Launches.Count == 1, "GUI candidate with mismatched " + mismatch + " must not succeed.");
            });

        await suite.Case("VM GUI target parser rejects duplicate aliases and unrelated config paths", () =>
        {
            var (vboxHost, vboxSource) = PolicyTests.VBox(); var vbox = vboxHost.Prepare(vboxSource);
            foreach (var alias in new[] { "--startvm", "-startvm", "-s" })
                suite.Check(!SpecialWindowService.MatchesTarget(vbox, [vbox.ExecutablePath, "--startvm", vbox.TargetId, alias, FakeSpecialWindowHost.OtherVmId]), "Every duplicate VM selector alias must be counted: " + alias);
            var (vmHost, vmSource) = PolicyTests.VMware(); var vm = vmHost.Prepare(vmSource);
            suite.Check(!SpecialWindowService.MatchesTarget(vm, [vm.ExecutablePath, FakeSpecialWindowHost.VmPath, @"C:\Other\Alpha.vmx"]), "Two configurations cannot prove one selected VM.");
            suite.Check(!SpecialWindowService.MatchesTarget(vm, [vm.ExecutablePath, @"C:\Other\Alpha.vmx"]), "Same config basename does not establish identity.");
        });

        await suite.Case("VMware matches configured displayName instead of config basename", async () =>
        {
            var (host, source) = PolicyTests.VMware(); var plan = host.Prepare(source);
            suite.Check(plan.TargetTitle == "Configured Alpha", "displayName from the validated VM config is used.");
            host.OnLaunch = _ => AddFrontend(host, plan, FakeSpecialWindowHost.Window("Configured Alpha - VMware Workstation"));
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(result.Success, "A VM whose displayName differs from its filename can show successfully.");
        });

        await suite.Case("VMware tab for a VM whose name merely contains the selected name cannot succeed", async () =>
        {
            var (host, source) = PolicyTests.VMware(); var plan = host.Prepare(source);
            host.OnLaunch = _ => AddFrontend(host, plan, FakeSpecialWindowHost.Window("Configured Alpha Copy - VMware Workstation"));
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success, "A manager's original argv and a substring of another VM name cannot prove the selected tab is visible.");
        });

        await suite.Case("VM titles require immediate legal delimiters instead of substring matches", () =>
        {
            foreach (var kind in new[] { SpecialWindowKind.VirtualBox, SpecialWindowKind.HyperV, SpecialWindowKind.VMware })
            {
                var (host, source) = Fixture(kind); var plan = host.Prepare(source); var name = plan.TargetTitle;
                suite.Check(SpecialWindowService.MatchesWindowTitle(plan, name), "The exact selected VM name is a valid title.");
                suite.Check(SpecialWindowService.MatchesWindowTitle(plan, GuiTitle(plan)), "The selected VM name with its known frontend title is accepted.");
                suite.Check(!SpecialWindowService.MatchesWindowTitle(plan, GuiTitle(plan with { TargetTitle = name + " Clone" })), "A longer VM name sharing the prefix must not match.");
                suite.Check(!SpecialWindowService.MatchesWindowTitle(plan, GuiTitle(plan with { TargetTitle = "Copy of " + name })), "Containing the selected name later in the title must not match.");
                suite.Check(!SpecialWindowService.MatchesWindowTitle(plan, GuiTitle(plan with { TargetTitle = name + "2" })), "A numeric suffix belongs to another VM name.");
                suite.Check(!SpecialWindowService.MatchesWindowTitle(plan, name + " - Unknown VM frontend"), "An arbitrary suffix cannot stand in for a known frontend title.");
                suite.Check(SpecialWindowService.MatchesWindowTitle(plan, name + " [Running] - Oracle VM VirtualBox") == (kind == SpecialWindowKind.VirtualBox), "Only VirtualBox allows a bracketed VM state after the name.");
                suite.Check(SpecialWindowService.MatchesWindowTitle(plan, name + " on " + Environment.MachineName + " - Virtual Machine Connection") == (kind == SpecialWindowKind.HyperV), "Only Hyper-V allows its English local-host delimiter.");
                suite.Check(SpecialWindowService.MatchesWindowTitle(plan, name + " 在 " + Environment.MachineName + " 上 - 虚拟机连接") == (kind == SpecialWindowKind.HyperV), "Only Hyper-V allows its localized local-host delimiter.");
            }
        });

        await suite.Case("final fresh capture rejects a disappearing window", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.OnLaunch = _ => AddFrontend(host, plan);
            host.OnCapture = () =>
            {
                if (host.Captures >= 5 && host.Processes.Count > 1)
                    host.Processes[1] = host.Processes[1] with { Windows = [] };
                return FakeSpecialWindowHost.Snapshot(host.Processes.ToArray());
            };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success, "Three earlier observations are insufficient after window disappearance.");
        });

        await suite.Case("final fresh capture must also recheck VM selection arguments", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.OnLaunch = _ => AddFrontend(host, plan);
            host.OnCapture = () =>
            {
                if (host.Captures >= 5 && host.Processes.Count > 1)
                    host.Arguments[host.Processes[1].Id] = [plan.ExecutablePath, "--startvm", FakeSpecialWindowHost.OtherVmId, "--separate"];
                return FakeSpecialWindowHost.Snapshot(host.Processes.ToArray());
            };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success, "A stale title/HWND must not bypass a changed VM target in final verification.");
        });

        await suite.Case("window disappearing during final VM provider check cannot report success", async () =>
        {
            var (host, source) = PolicyTests.VBox(); var plan = host.Prepare(source);
            host.OnLaunch = _ => AddFrontend(host, plan);
            host.OnReadVm = (p, _, _) =>
            {
                if (host.Launches.Count > 0 && host.Processes.Count > 1)
                    host.Processes[1] = host.Processes[1] with { Windows = [] };
                return new(FakeSpecialWindowHost.VmId, "Alpha", p.Id);
            };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success, "Potentially slow VM provider calls require a subsequent fresh window check.");
        });

        await suite.Case("already-visible background app window does not prove the recovery request worked", async () =>
        {
            var (host, source) = Fixture(SpecialWindowKind.TrayApplication); var plan = host.Prepare(source);
            AddFrontend(host, plan);
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(!result.Success, "A prior unfocused window cannot be reported as recovered.");
        });

        await suite.Case("previous app window reaching foreground is valid recovery evidence", async () =>
        {
            var (host, source) = Fixture(SpecialWindowKind.TrayApplication); var plan = host.Prepare(source);
            var frontend = AddFrontend(host, plan);
            host.OnLaunch = _ => host.Processes[^1] = frontend with { Windows = [frontend.Windows[0] with { IsForeground = true }] };
            var result = await Service(host).ExecuteAsync(plan, true);
            suite.Check(result.Success, "A verified existing main window can be recovered into the foreground.");
        });
    }

    private static (FakeSpecialWindowHost Host, ProcessRecord Source) Fixture(SpecialWindowKind kind)
    {
        if (kind == SpecialWindowKind.VirtualBox) return PolicyTests.VBox();
        if (kind == SpecialWindowKind.HyperV) return PolicyTests.HyperV();
        if (kind == SpecialWindowKind.VMware) return PolicyTests.VMware();
        var host = new FakeSpecialWindowHost(); return (host, host.Add("Telegram.exe"));
    }

    private static ProcessRecord AddFrontend(FakeSpecialWindowHost host, SpecialWindowPlan plan, WindowRecord? window = null)
    {
        bool isDefaultWindow = window is null;
        window ??= FakeSpecialWindowHost.Window(GuiTitle(plan));
        if (plan.Kind == SpecialWindowKind.VirtualBox && isDefaultWindow) window = window with { ClassName = "Qt5152QWindowIcon" };
        var frontend = host.Add(Path.GetFileName(plan.ExecutablePath), plan.ExecutablePath, 602, plan.Arguments.ToArray()) with { Windows = [window] };
        host.Processes[^1] = frontend;
        return frontend;
    }

    private static string GuiTitle(SpecialWindowPlan plan) => plan.Kind switch
    {
        SpecialWindowKind.VirtualBox => plan.TargetTitle + " [Running] - Oracle VM VirtualBox",
        SpecialWindowKind.HyperV => plan.TargetTitle + " on " + Environment.MachineName + " - Virtual Machine Connection",
        SpecialWindowKind.VMware => plan.TargetTitle + " - VMware Workstation",
        _ => "Fixture app"
    };
}
