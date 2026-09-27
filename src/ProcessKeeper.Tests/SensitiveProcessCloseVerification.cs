using ProcessKeeper.Core;

public static class SensitiveProcessCloseVerification
{
    public static void Run(Action<bool, string> check)
    {
        const string sid = "S-1-5-21-100-200-300-1001";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var policy = new ProtectionPolicy(1, sid, 999);
        var desktop = new ProcessRecord
        {
            Id = 12345, Name = "explorer.exe", Path = Path.Combine(windows, "explorer.exe"),
            SessionId = 1, OwnerSid = sid, StartTimeUtcTicks = DateTime.UtcNow.AddMinutes(-1).Ticks,
            NativeCritical = false, IsSystem = true, ApplicationKey = "fixture:explorer"
        };
        ProcessSnapshot Snapshot(params ProcessRecord[] processes) => new(DateTimeOffset.UtcNow, processes, []);
        bool Protected(ProcessRecord process, IReadOnlyList<WhitelistRule>? rules = null, ProcessRecord[]? observed = null) =>
            policy.EvaluateSensitiveClose(process, Snapshot(observed ?? new[] { process }), rules ?? []).Protected;
        check(!Protected(desktop), "explicit sensitive policy admits verified own desktop identity only");
        check(policy.Evaluate(desktop, Snapshot(desktop), []).Protected, "ordinary bulk policy still protects desktop after explicit policy exists");
        check(SensitiveProcessClose.IsSensitiveComponent(desktop), "exact Windows Explorer path is recognized");
        check(SensitiveProcessClose.IsSensitiveComponent(desktop with { Name = "EXPLORER.EXE", Path = desktop.Path.ToUpperInvariant() }),
            "desktop identity uses case-insensitive Windows path matching");
        foreach (var relative in new[]
        {
            @"SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe",
            @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",
            @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe",
            @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe",
            @"SystemApps\InputApp_cw5n1h2txyewy\TextInputHost.exe",
            @"System32\ctfmon.exe"
        })
        {
            var component = desktop with { Name = Path.GetFileName(relative), Path = Path.Combine(windows, relative) };
            check(SensitiveProcessClose.IsSensitiveComponent(component) && !Protected(component), "narrow installed desktop component may be explicitly selected: " + component.Name);
            var unclassified = component with { IsSystem = false };
            check(policy.Evaluate(unclassified, Snapshot(unclassified), []).Protected, "ordinary policy protects even unclassified desktop names: " + component.Name);
        }
        foreach (var path in new[] { @"C:\Users\someone\explorer.exe", @"D:\Windows\explorer.exe", windows + @"-copy\explorer.exe",
            Path.Combine(windows, @"Temp\explorer.exe"), Path.Combine(windows, @"System32\explorer.exe"),
            Path.Combine(windows, @"System32\..\explorer.exe"), desktop.Path + ":payload", desktop.Path + " ",
            @"\\server\Windows\explorer.exe", "explorer.exe", "", desktop.Path + "\0" })
            check(Protected(desktop with { Path = path }) && !SensitiveProcessClose.IsSensitiveComponent(desktop with { Path = path }),
                "same-name masquerade, noncanonical or incomplete path is refused");
        check(Protected(desktop with { Name = "other.exe" }), "name/path disagreement is refused");
        foreach (var name in new[] { "svchost.exe", "lsass.exe", "winlogon.exe", "csrss.exe", "services.exe", "dwm.exe", "sihost.exe",
            "SearchIndexer.exe", "RuntimeBroker.exe", "taskhostw.exe", "conhost.exe", "SecurityHealthService.exe" })
        {
            var core = desktop with { Name = name, Path = Path.Combine(windows, "System32", name), IsSystem = false };
            check(Protected(core), "native noncritical result never broadens allowlist to: " + name);
        }
        check(Protected(desktop with { NativeCritical = true }), "native critical process cannot receive desktop exception");
        check(Protected(desktop with { NativeCritical = null }), "unknown native critical state fails closed");
        check(Protected(desktop with { IsSystem = false, NativeCritical = null }), "clearing display system flag cannot substitute for native verification");
        check(Protected(desktop with { SessionId = 0 }), "Session 0 stays protected");
        check(Protected(desktop with { SessionId = 2 }), "another session stays protected");
        check(Protected(desktop with { OwnerSid = "S-1-5-18" }), "SYSTEM owner stays protected");
        check(Protected(desktop with { OwnerSid = "S-1-5-21-other" }), "another user stays protected");
        check(Protected(desktop with { OwnerSid = "" }), "unknown owner stays protected");
        check(new ProtectionPolicy(1, "", 999).EvaluateSensitiveClose(desktop, Snapshot(desktop), []).Protected,
            "unknown acting user cannot authorize sensitive close");
        check(Protected(desktop with { Id = 4 }), "reserved system PID stays protected");
        check(Protected(desktop with { Id = 999 }), "manager PID stays protected");
        check(Protected(desktop with { IsSelf = true }), "self marker stays protected");
        check(Protected(desktop with { StartTimeUtcTicks = 0 }), "unknown creation time stays protected");
        check(Protected(desktop with { StartTimeUtcTicks = DateTime.MaxValue.Ticks }), "future creation time stays protected");
        check(Protected(desktop with { Services = new[] { new ServiceRecord("fixture", "fixture", desktop.Id) } }),
            "service association blocks sensitive close even on matching desktop path");
        check(Protected(desktop, observed: []), "missing frozen identity cannot be expanded to a different target");
        check(Protected(desktop, observed: [desktop, desktop]), "duplicate observed PID is ambiguous and fails closed");
        foreach (var changed in new[]
        {
            desktop with { StartTimeUtcTicks = desktop.StartTimeUtcTicks + 1 }, desktop with { Path = Path.Combine(windows, @"Temp\explorer.exe") },
            desktop with { Name = "other.exe" }, desktop with { OwnerSid = "S-1-5-18" }, desktop with { SessionId = 2 },
            desktop with { NativeCritical = true }, desktop with { NativeCritical = null }, desktop with { IsSelf = true },
            desktop with { Services = new[] { new ServiceRecord("fixture", "fixture", desktop.Id) } }
        }) check(Protected(desktop, observed: [changed]), "snapshot identity/state change after confirmation blocks sensitive close");
        foreach (var rule in new[]
        {
            new WhitelistRule { Kind = RuleKind.ProcessName, Value = desktop.Name },
            new WhitelistRule { Kind = RuleKind.ExecutablePath, Value = desktop.Path },
            new WhitelistRule { Kind = RuleKind.Directory, Value = windows },
            new WhitelistRule { Kind = RuleKind.Application, Value = desktop.ApplicationKey }
        }) check(Protected(desktop, [rule]), "enabled whitelist rule blocks independently confirmed desktop close: " + rule.Kind);
        var appRule = new WhitelistRule { Kind = RuleKind.Application, Value = "new:protected" };
        check(Protected(desktop, [appRule], [desktop with { ApplicationKey = appRule.Value }]), "newly observed application whitelist is rechecked");
        check(Protected(desktop with { ApplicationKey = appRule.Value }, [appRule], [desktop]), "originally confirmed application whitelist is also rechecked");
        check(!Protected(desktop, [new WhitelistRule { Kind = RuleKind.ProcessName, Value = desktop.Name, Enabled = false }]),
            "disabled whitelist rule does not invent a protection exception");
        var parent = desktop with { Id = 500, Name = "allowed.exe", Path = @"C:\Fixture\allowed.exe", ApplicationKey = "fixture:allowed",
            IsSystem = false, StartTimeUtcTicks = desktop.StartTimeUtcTicks - 100 };
        var child = desktop with { ParentId = parent.Id };
        var descendantRule = new WhitelistRule { Kind = RuleKind.Application, Value = parent.ApplicationKey, IncludeDescendants = true };
        check(Protected(child, [descendantRule], [child, parent]), "verified allowlisted ancestor still protects sensitive desktop child");
        check(!Protected(child, [descendantRule], [child, parent with { StartTimeUtcTicks = child.StartTimeUtcTicks + 1 }]),
            "reused parent PID does not invent an ancestor association");
        var identified = ProcessIdentity.Classify(desktop with { IsSystem = false });
        check(identified.IsSystem && identified.NativeCritical == false && !Protected(identified),
            "display classification preserves independent verified noncritical native evidence");
        check(ProcessIdentity.Classify(desktop with { NativeCritical = null }).NativeCritical is null &&
            Protected(ProcessIdentity.Classify(desktop with { NativeCritical = null })), "classification never fabricates native evidence for missing query");
    }
}
