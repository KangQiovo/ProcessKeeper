using ProcessKeeper.Core;

internal static class PolicyTests
{
    internal static async Task Run(SpecialWindowSuite suite)
    {
        var trayEntries = new (string Source, string Entry, string[] Arguments)[]
        {
            ("clash-verge.exe", "clash-verge.exe", []),
            ("steam.exe", "steam.exe", ["-open", "steam://open/main"]),
            ("QQ.exe", "QQ.exe", []),
            ("GameViewer.exe", "GameViewer.exe", []),
            ("HipsTray.exe", "HipsMain.exe", []),
            ("HipsMain.exe", "HipsMain.exe", []),
            ("Docker Desktop.exe", "Docker Desktop.exe", []),
            ("qbittorrent.exe", "qbittorrent.exe", []),
            ("Telegram.exe", "Telegram.exe", []),
            ("Discord.exe", "Discord.exe", []),
            ("WeChat.exe", "WeChat.exe", []),
            ("Weixin.exe", "Weixin.exe", []),
            ("SyncTrayzor.exe", "SyncTrayzor.exe", [])
        };
        foreach (var row in trayEntries)
            await suite.Case("tray application entry: " + row.Source, () =>
            {
                var host = new FakeSpecialWindowHost();
                var source = host.Add(row.Source, arguments: ["--fixture-original-value"]);
                var executable = Path.Combine(Path.GetDirectoryName(source.Path)!, row.Entry);
                host.AddFile(executable);
                var plan = host.Prepare(source);
                suite.Check(plan.Kind == SpecialWindowKind.TrayApplication, "Tray entry must remain an application plan.");
                suite.Check(SpecialWindowPolicy.SamePath(plan.ExecutablePath, executable), "App-specific executable entry must match.");
                suite.Check(plan.Arguments.SequenceEqual(row.Arguments), "Only the documented per-app arguments are emitted.");
                suite.Check(plan.SourceArguments.SequenceEqual(host.Arguments[source.Id]), "Source command line remains available for revalidation.");
                suite.Check(host.Launches.Count == 0, "Preparation cannot launch the target.");
            });

        foreach (string backendFolder in new[] { "resources", @"resources\bin" })
            await suite.Case("Docker backend maps to desktop frontend: " + backendFolder, () =>
            {
                var host = new FakeSpecialWindowHost();
                var source = host.Add("com.docker.backend.exe", @"C:\Program Files\Docker\Docker\" + backendFolder + @"\com.docker.backend.exe");
                const string desktop = @"C:\Program Files\Docker\Docker\Docker Desktop.exe";
                host.AddFile(desktop);
                var plan = host.Prepare(source);
                suite.Check(SpecialWindowPolicy.SamePath(plan.ExecutablePath, desktop), "Backend never launches itself as a GUI.");
            });

        foreach (string argument in new[] { "--type=renderer", "--type=gpu-process", "--type", "--service", "--TYPE=utility" })
            await suite.Case("renderer/service rejected: " + argument, () =>
            {
                var host = new FakeSpecialWindowHost();
                var source = host.Add("Discord.exe", arguments: [argument]);
                suite.Check(SpecialWindowPolicy.Prepare(source, host.Arguments[source.Id], host) is null, "A subprocess role is not a GUI entry.");
            });

        await suite.Case("unknown backend and shell are not guessed GUI entry points", () =>
        {
            foreach (string name in new[] { "cmd.exe", "pwsh.exe", "powershell.exe", "mihomo.exe", "VBoxSVC.exe", "vmrun.exe", "qemu-system-x86_64.exe" })
            {
                var host = new FakeSpecialWindowHost(); var source = host.Add(name);
                suite.Check(!SpecialWindowPolicy.IsSupportedName(name), "Unsupported process name must stay unsupported: " + name);
                suite.Check(SpecialWindowPolicy.Prepare(source, host.Arguments[source.Id], host) is null, "No inferred launch for " + name);
            }
        });

        await suite.Case("unverified process identity and argv[0] reject a plan", () =>
        {
            var host = new FakeSpecialWindowHost(); var source = host.Add("Telegram.exe");
            foreach (var invalid in new[]
            {
                source with { Id = 4 }, source with { StartTimeUtcTicks = 0 },
                source with { Path = "Telegram.exe" }, source with { Path = @"C:\Other\not-telegram.exe" }
            }) suite.Reject(() => SpecialWindowPolicy.Prepare(invalid, [invalid.Path], host), "Incomplete source identity must be rejected.");
            suite.Reject(() => SpecialWindowPolicy.Prepare(source, [], host), "Empty arguments cannot identify the original entry.");
            suite.Reject(() => SpecialWindowPolicy.Prepare(source, [@"C:\Elsewhere\Telegram.exe"], host), "Same basename at a different path is not the same source.");
        });

        await suite.Case("frontend must exist and carry the GUI executable subsystem", () =>
        {
            var host = new FakeSpecialWindowHost(); var source = host.Add("HipsTray.exe");
            suite.Reject(() => host.Prepare(source), "Missing frontend cannot create a plan.");
            var frontend = Path.Combine(Path.GetDirectoryName(source.Path)!, "HipsMain.exe");
            host.AddFile(frontend, gui: false);
            suite.Reject(() => host.Prepare(source), "Console-subsystem frontend cannot create a plan.");
            host.GuiFiles.Add(frontend);
            suite.Check(host.Prepare(source).ExecutablePath == frontend, "Actual GUI subsystem produces a plan.");
        });

        await suite.Case("prepared argument collections do not alias mutable inputs", () =>
        {
            var host = new FakeSpecialWindowHost(); var source = host.Add("Telegram.exe");
            var args = new[] { source.Path, "-original" };
            var plan = SpecialWindowPolicy.Prepare(source, args, host)!;
            args[1] = "-changed";
            suite.Check(plan.SourceArguments[1] == "-original", "Later caller mutation cannot alter prepared source arguments.");
            suite.Check(plan.SourceArguments is not string[] && plan.Arguments is not string[], "Prepared argument lists cannot be directly cast to mutable arrays.");
        });

        await suite.Case("VirtualBox selects one exact running UUID and attaches without power controls", () =>
        {
            var (host, source) = VBox();
            var plan = host.Prepare(source);
            suite.Check(plan.TargetId == FakeSpecialWindowHost.VmId && plan.TargetTitle == "Alpha", "VM UUID and name are anchored to the API mapping.");
            suite.Check(plan.Arguments.SequenceEqual(["--startvm", FakeSpecialWindowHost.VmId, "--separate"]), "Only the separate frontend attach invocation is emitted.");
            suite.Check(Path.GetFileName(plan.ExecutablePath) == "VirtualBoxVM.exe", "Headless engine is never the GUI executable.");
        });

        foreach (string[] arguments in new[]
        {
            Array.Empty<string>(), new[] { "--startvm" }, new[] { "--startvm", "Alpha" },
            new[] { "--startvm", FakeSpecialWindowHost.VmId, "-s", FakeSpecialWindowHost.VmId },
            new[] { "--startvm", FakeSpecialWindowHost.VmId, "--startvm", FakeSpecialWindowHost.OtherVmId }
        })
            await suite.Case("VirtualBox rejects absent, named, or duplicate UUID: " + string.Join(" ", arguments), () =>
            {
                var (host, source) = VBox(); host.Arguments[source.Id] = [source.Path, .. arguments];
                suite.Reject(() => host.Prepare(source), "VM target must be unique and parse as a UUID.");
            });

        await suite.Case("VirtualBox mapping must match both source PID and requested UUID", () =>
        {
            var (host, source) = VBox();
            foreach (VirtualMachineTarget? invalid in new VirtualMachineTarget?[]
            {
                null, new(FakeSpecialWindowHost.OtherVmId, "Alpha", source.Id),
                new(FakeSpecialWindowHost.VmId, "Alpha", source.Id + 1),
                new(FakeSpecialWindowHost.VmId, "", source.Id)
            })
            {
                host.OnReadVm = (_, _, _) => invalid;
                suite.Reject(() => host.Prepare(source), "Wrong or ambiguous VM backend mapping must be rejected.");
            }
        });

        await suite.Case("Hyper-V requires system path, session zero, and PID-to-GUID mapping", () =>
        {
            var (host, source) = HyperV();
            var plan = host.Prepare(source);
            suite.Check(plan.Kind == SpecialWindowKind.HyperV && plan.Arguments.SequenceEqual(["localhost", "-G", FakeSpecialWindowHost.VmId]), "VMConnect selects the exact GUID.");
            suite.Check(Path.GetFileName(plan.ExecutablePath) == "vmconnect.exe", "Hyper-V uses a GUI connector.");
            suite.Check(SpecialWindowPolicy.Prepare(source with { SessionId = 1 }, [], host) is null, "Interactive impostor worker is rejected.");
            suite.Check(SpecialWindowPolicy.Prepare(source with { Path = @"C:\Other\vmwp.exe" }, [], host) is null, "Worker outside system directory is rejected.");
            host.OnReadVm = (_, _, _) => new(FakeSpecialWindowHost.VmId, "Alpha", source.Id + 1);
            suite.Reject(() => host.Prepare(source), "A GUID mapped to another worker PID cannot be launched.");
        });

        foreach (bool x64 in new[] { false, true })
            await suite.Case("VMware opens exact config without power-on flags, x64=" + x64, () =>
            {
                var (host, source) = VMware(x64);
                var plan = host.Prepare(source);
                suite.Check(plan.Arguments.SequenceEqual([FakeSpecialWindowHost.VmPath]), "Only the exact original .vmx path is forwarded.");
                suite.Check(plan.TargetId == FakeSpecialWindowHost.VmPath, "Target identity remains the complete config path.");
                suite.Check(SpecialWindowPolicy.SamePath(plan.ExecutablePath, @"C:\Fixture Apps\VMware\vmware.exe"), "Correct frontend is chosen for either installation layout.");
            });

        await suite.Case("VMware rejects missing, relative, and duplicate configuration paths", () =>
        {
            var (host, source) = VMware();
            foreach (string[] arguments in new[]
            {
                Array.Empty<string>(), new[] { "Alpha.vmx" }, new[] { @"C:\Missing\Alpha.vmx" },
                new[] { FakeSpecialWindowHost.VmPath, FakeSpecialWindowHost.VmPath },
                new[] { FakeSpecialWindowHost.VmPath, @"C:\Fixture VMs\Beta\Beta.vmx" }
            })
            {
                host.Arguments[source.Id] = [source.Path, .. arguments];
                suite.Reject(() => host.Prepare(source), "Exactly one absolute existing VM configuration is required.");
            }
        });

        var window = FakeSpecialWindowHost.Window();
        await suite.Case("a visible, titled, bounded main HWND qualifies for inspection", () =>
            suite.Check(SpecialWindowPolicy.IsRealGuiWindow(window), "A proper GUI main window should be accepted by geometry/style filters."));
        var invalidWindows = new (string Kind, WindowRecord Window)[]
        {
            ("zero HWND", window with { Handle = 0 }), ("child", window with { IsTopLevel = false }),
            ("hidden", window with { IsVisible = false }), ("minimized", window with { IsMinimized = true }),
            ("cloaked", window with { IsCloaked = true }), ("tool", window with { IsToolWindow = true }),
            ("zero bounds", window with { HasUsableBounds = false }), ("no main style", window with { HasCaption = false, HasAppWindowStyle = false }),
            ("blank title", window with { Title = "  " }), ("console", window with { ClassName = "ConsoleWindowClass" }),
            ("terminal", window with { ClassName = "CASCADIA_HOSTING_WINDOW_CLASS" }),
            ("pseudo console", window with { ClassName = "PseudoConsoleWindow" }),
            ("dialog", window with { ClassName = "#32770" }), ("popup menu", window with { ClassName = "#32768" }),
            ("tooltip", window with { ClassName = "tooltips_class32" })
        };
        foreach (var invalid in invalidWindows)
            await suite.Case("non-main window is rejected: " + invalid.Kind, () =>
                suite.Check(!SpecialWindowPolicy.IsRealGuiWindow(invalid.Window), "Unusable window cannot count as GUI success."));
    }

    internal static (FakeSpecialWindowHost Host, ProcessRecord Source) VBox()
    {
        var host = new FakeSpecialWindowHost();
        var source = host.Add("VBoxHeadless.exe", arguments: ["--startvm", FakeSpecialWindowHost.VmId]);
        host.AddFile(Path.Combine(Path.GetDirectoryName(source.Path)!, "VirtualBoxVM.exe"));
        host.OnReadVm = (p, _, _) => new(FakeSpecialWindowHost.VmId, "Alpha", p.Id);
        return (host, source);
    }

    internal static (FakeSpecialWindowHost Host, ProcessRecord Source) HyperV()
    {
        var host = new FakeSpecialWindowHost();
        var source = host.Add("vmwp.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmwp.exe")) with { SessionId = 0, OwnerSid = "S-1-5-18" };
        host.Processes[0] = source;
        host.AddFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe"));
        host.OnReadVm = (p, _, _) => new(FakeSpecialWindowHost.VmId, "Alpha", p.Id);
        return (host, source);
    }

    internal static (FakeSpecialWindowHost Host, ProcessRecord Source) VMware(bool x64 = false)
    {
        var host = new FakeSpecialWindowHost();
        var source = host.Add("vmware-vmx.exe", @"C:\Fixture Apps\VMware\" + (x64 ? @"x64\" : "") + "vmware-vmx.exe", arguments: ["-s", FakeSpecialWindowHost.VmPath]);
        host.AddFile(FakeSpecialWindowHost.VmPath, gui: false);
        host.AddFile(@"C:\Fixture Apps\VMware\vmware.exe");
        host.OnReadVm = (p, _, path) => new(path, "Configured Alpha", p.Id);
        return (host, source);
    }
}
