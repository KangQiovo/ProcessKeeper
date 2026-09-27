using ProcessKeeper.Core;
using System.Text.Json;

public static class AutorunOwnershipVerification
{
    public static void Run(Action<bool, string> check)
    {
        var probe = new Probe();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var classifier = new AutorunOwnershipClassifier(probe, @"C:\Windows", () => now);
        AutorunEntry Entry(string path, AutorunSourceKind kind = AutorunSourceKind.Service) => new()
        {
            Id = path, SourceKind = kind, Command = path, TargetPath = path, IsSystem = true,
            CanChange = false, Enabled = true, Fingerprint = "unchanged", Locator = new() { Path = "synthetic" }
        };
        AutorunEntry Classify(AutorunEntry entry) => classifier.Classify(new[] { entry })[0];
        var windows = Entry(@"C:\Windows\System32\fixture-service.exe");
        var classified = Classify(windows);
        check(classified.Ownership == AutorunOwnership.Windows, "WRP-protected direct component is classified Windows");
        check(classified with { Ownership = AutorunOwnership.Unknown } == windows, "attribution changes no identity, state or mutation protection");
        check(!JsonSerializer.Serialize(classified).Contains("Ownership"), "display attribution never persists in configuration backups");
        var calls = probe.ProtectionReads;
        check(Classify(windows).Ownership == AutorunOwnership.Windows && probe.ProtectionReads == calls,
            "unchanged present file reuses bounded evidence cache");
        probe.Identity = probe.Identity! with { Length = 21 };
        Classify(windows);
        check(probe.ProtectionReads == calls + 1, "changed file identity invalidates attribution cache");
        now = now.AddMinutes(6); Classify(windows);
        check(probe.ProtectionReads == calls + 2, "cached evidence expires after five minutes");
        probe.Identity = null;
        check(Classify(windows).Ownership == AutorunOwnership.Unknown, "missing file invalidates an earlier Windows classification");
        probe.Identity = new(22, 2, 2); Classify(windows);
        check(probe.ProtectionReads == calls + 3, "file restored after absence is probed again");
        probe.Protected = false;
        check(Classify(Entry(@"C:\Windows\System32\drivers\vendor.sys", AutorunSourceKind.Driver)).Ownership == AutorunOwnership.Unknown,
            "third-party driver under Windows is visible without WRP evidence");
        check(Classify(Entry(@"D:\Vendor\service.exe")).Ownership == AutorunOwnership.Unknown,
            "readonly third-party service remains visible despite IsSystem protection");
        probe.Protected = true;
        check(Classify(Entry(@"C:\Windows\System32\drivers\os.sys", AutorunSourceKind.Driver)).Ownership == AutorunOwnership.Windows,
            "WRP-protected direct Windows driver may be hidden by system filter");
        check(Classify(Entry(@"System32\drivers\relative.sys", AutorunSourceKind.Driver) with { TargetPath = "" }).Ownership == AutorunOwnership.Windows,
            "kernel driver System32 relative path resolves only beneath Windows");
        foreach (var host in new[] { "cmd.exe", "powershell.exe", "pwsh.exe", "svchost.exe", "rundll32.exe", "dllhost.exe", "wscript.exe", "cscript.exe", "mshta.exe" })
        {
            calls = probe.ProtectionReads;
            check(Classify(Entry(@"C:\Windows\System32\" + host)).Ownership == AutorunOwnership.Unknown && probe.ProtectionReads == calls,
                "shared host stays visible without examining its protected executable: " + host);
        }
        foreach (var kind in new[] { AutorunSourceKind.WmiSubscription, AutorunSourceKind.AdvancedRegistry,
            AutorunSourceKind.ScheduledTask, AutorunSourceKind.StartupFolder })
            check(Classify(windows with { SourceKind = kind }).Ownership == AutorunOwnership.Unknown,
                "extension/action source does not inherit Windows ownership from its target: " + kind);
        foreach (var command in new[] { windows.TargetPath + " --plugin D:\\Vendor\\addon.dll", '"' + windows.TargetPath + "\" /c other.exe",
            windows.TargetPath + "\nD:\\Vendor\\other.exe", @"C:\Windows-old\fixture.exe", @"\\server\Windows\fixture.exe",
            @"C:\Windows\System32\fixture.exe:payload", @"C:\Windows\System32\..\..\Vendor\fixture.exe" })
            check(Classify(windows with { Command = command, TargetPath = "" }).Ownership == AutorunOwnership.Unknown,
                "arguments, extra actions and ambiguous/nonlocal paths remain visible");
        check(Classify(windows with { Command = '"' + windows.TargetPath + '"' }).Ownership == AutorunOwnership.Windows,
            "quoted argument-free Windows executable retains direct attribution");
        check(Classify(windows with { TargetPath = @"C:\Windows\System32\another.exe" }).Ownership == AutorunOwnership.Unknown,
            "mismatched command/target evidence stays visible");
        check(Classify(windows with { SourceKind = AutorunSourceKind.PackagedStartup, Ownership = AutorunOwnership.Windows }).Ownership == AutorunOwnership.Windows,
            "live package System signature evidence is preserved");
        check(Classify(windows with { SourceKind = AutorunSourceKind.PackagedStartup, Ownership = AutorunOwnership.Unknown }).Ownership == AutorunOwnership.Unknown,
            "non-system package is not inferred to be a Windows component");
        check(Classify(windows with { SourceKind = AutorunSourceKind.WmiSubscription, Ownership = AutorunOwnership.Windows }).Ownership == AutorunOwnership.Unknown,
            "stale nonpackage display attribution is recomputed conservatively");
        calls = probe.ProtectionReads;
        check(classifier.Classify(new[] { windows }, budgetAvailable: () => false)[0].Ownership == AutorunOwnership.Unknown && probe.ProtectionReads == calls,
            "exhausted scan budget retains entries and performs no evidence query");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { classifier.Classify(new[] { windows }, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled && probe.ProtectionReads == calls, "pre-cancelled attribution performs no evidence query");
        var cancelProbe = new Probe(); using var during = new CancellationTokenSource();
        cancelProbe.AfterProtection = () => during.Cancel(); cancelled = false;
        try { new AutorunOwnershipClassifier(cancelProbe, @"C:\Windows").Classify(new[] { windows }, during.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "cancellation observed after native evidence collection is propagated");
        var raceProbe = new Probe(); raceProbe.AfterProtection = () => raceProbe.Identity = new(99, 4, 5);
        check(new AutorunOwnershipClassifier(raceProbe, @"C:\Windows").Classify(new[] { windows })[0].Ownership == AutorunOwnership.Unknown,
            "file changed during collection is not classified as Windows");
        var errorProbe = new Probe { Throw = true };
        check(new AutorunOwnershipClassifier(errorProbe, @"C:\Windows").Classify(new[] { windows })[0].Ownership == AutorunOwnership.Unknown,
            "unavailable native evidence fails visible instead of hiding entry");
        var boundProbe = new Probe();
        var many = Enumerable.Range(0, AutorunOwnershipClassifier.MaximumFileChecks + 10)
            .Select(index => Entry(@"C:\Windows\System32\fixture-" + index + ".exe")).ToArray();
        var bounded = new AutorunOwnershipClassifier(boundProbe, @"C:\Windows").Classify(many);
        check(bounded.Count == many.Length && boundProbe.ProtectionReads <= AutorunOwnershipClassifier.MaximumFileChecks &&
            bounded.Last().Ownership == AutorunOwnership.Unknown, "file work is bounded while every unclassified entry remains visible");
        check(bounded.Select(item => item.Id).SequenceEqual(many.Select(item => item.Id)), "classification preserves snapshot order and ownership association");
    }

    private sealed class Probe : IAutorunOwnershipProbe
    {
        public AutorunOwnershipFile? Identity = new(20, 1, 1);
        public bool Protected = true, Throw;
        public int ProtectionReads;
        public Action? AfterProtection;
        public AutorunOwnershipFile? ReadIdentity(string path, CancellationToken token) => Identity;
        public bool IsWindowsProtected(string path, CancellationToken token)
        {
            ProtectionReads++;
            if (Throw) throw new IOException("synthetic unavailable evidence");
            AfterProtection?.Invoke(); return Protected;
        }
    }
}
