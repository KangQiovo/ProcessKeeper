using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using ProcessKeeper.Core;

L.Language = "zh-Hans";
var count = 0;
void Check(bool condition, string description)
{ if (!condition) throw new Exception("FAIL: " + description); count++; Console.WriteLine("PASS: " + description); }
Check(IntPtr.Size == 4, "fixture actually runs in a 32-bit CLR process");
LegacyCapabilitiesVerification.Run(Check);
TimeDisplayVerification.Run(Check);
BuildInfoVerification.Run(Check);
AutorunSafetyVerification.Run(Check);
AutorunAdvancedVerification.Run(Check);
AutorunApprovalVerification.Run(Check);
AutorunOwnershipVerification.Run(Check);
SensitiveProcessCloseVerification.Run(Check);
RiskConfirmationModeVerification.Run(Check);
ReleaseBoundaryVerification.Run(Check);
WhitelistProfilesVerification.Run(Check);
count += AutorunSearchVerification.Run();
InstalledSearchVerification.Run(Check, Console.WriteLine);
ApplicationGroupingVerification.Run(Check);
var fixtureRoot = Path.Combine(Path.GetTempPath(), "ProcessKeeper-legacy-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
try
{
    Check(WhitelistStore.CreateDefaults().Count == 0, "compatibility releases start with an empty personal whitelist");
    var defaults = HistoricalWhitelistFixture.Create();
    Check(defaults.Count == 7 && defaults.Single(item => item.Value == "known:codex").IncludeDescendants, "historical personal rules and their descendants remain portable fixture data");
    var local = new WhitelistRule { Id = "same-id", Name = "Fixture local app", Kind = RuleKind.ExecutablePath, Value = @"Z:\MissingOnOtherComputer\fixture.exe" };
    var portable = defaults[0] with { Id = "same-id" };
    var prepared = RulePortability.PrepareImport([local, portable]);
    Check(!prepared[0].Enabled && prepared[1].Enabled, "import disables unavailable machine-specific path rules without disabling portable identities");
    Check(RulePortability.ForSharing([local, portable]).SequenceEqual([portable]), "share export excludes machine-specific paths");
    var merged = WhitelistStore.MergeRules([portable], [local]);
    Check(merged.Count == 2 && merged[0].Id != merged[1].Id, "import rekeys ID collisions without losing different rule meanings");
    var store = new WhitelistStore(fixtureRoot);
    store.Save(defaults); store.Save(merged);
    Check(store.Load().SequenceEqual(merged) && File.Exists(store.FilePath + ".bak"), "Framework JSON and atomic replace preserve rules and previous good backup");
    var old = File.ReadAllText(store.FilePath);
    File.WriteAllText(store.FilePath, "{\"Version\":999,\"Rules\":[]}");
    var rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && File.ReadAllText(store.FilePath).Contains("999"), "unknown imported schema refuses loading and preserves its original file");
    File.WriteAllText(store.FilePath, old);
    var sid = "S-1-5-21-100-200-300-1001";
    var process = new ProcessRecord { Id = 7123, StartTimeUtcTicks = DateTime.UtcNow.Ticks, SessionId = 1, OwnerSid = sid,
        Name = "fixture.exe", Path = @"C:\Fixture\fixture.exe", ApplicationKey = "known:clash" };
    var snapshot = new ProcessSnapshot(DateTimeOffset.Now, [process], []);
    var protection = new ProtectionPolicy(1, sid, 9123);
    Check(protection.Evaluate(process, snapshot, defaults).Protected, "shared rules protect matching portable app identities");
    Check(protection.Evaluate(process with { IsSystem = true }, snapshot, []).Protected, "system processes remain protected even with empty whitelist");
    Check(protection.Evaluate(process with { OwnerSid = "" }, snapshot, []).Protected, "unknown owner identity remains protected");
    var arguments = new[] { "", "plain", "spaces in path", @"C:\space folder\", "quote\"inside", @"two\\\" + "\"end", "中文参数", "`$()&|<>%" };
    var info = new ProcessStartInfo(); foreach (var argument in arguments) LegacyCompat.AddArgument(info, argument);
    Check(ArgumentParser.Read("fixture.exe " + info.Arguments).Skip(1).SequenceEqual(arguments), "legacy argument encoding round-trips empty, quotes, trailing backslashes and shell metacharacters without a shell");
    var before = info.Arguments; rejected = false; try { LegacyCompat.AddArgument(info, "bad\0argument"); } catch (ArgumentException) { rejected = true; }
    Check(rejected && info.Arguments == before, "invalid process arguments are rejected before launch configuration changes");
    Check(LegacyCompat.GetRelativePath(@"C:\Fixture", @"C:\Fixture\literal%20#name\app.exe") == @"literal%20#name\app.exe", "relative paths preserve literal percent and hash characters without URL decoding");
    Check(LegacyCompat.GetRelativePath(@"C:\Fixture", @"D:\Other\app.exe") == @"D:\Other\app.exe", "relative paths on another drive stay absolute");
    var backups = new AutorunBackupStore(Path.Combine(fixtureRoot, "backups"));
    var location = new AutorunLocator { Hive = "HKCU", View = 32, Path = @"Software\Microsoft\Windows\CurrentVersion\RunOnce", ValueName = "!*Fixture" };
    var state = new AutorunState(true, true, "Raw ExpandString payload");
    var entry = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.RegistryRunOnce, location), SourceKind = AutorunSourceKind.RegistryRunOnce,
        Locator = location, Fingerprint = state.Fingerprint, Enabled = true, CanChange = true };
    backups.Save(new(entry, state, new(false, false, "")));
    var changed = state with { Payload = "Changed exact raw payload" };
    var second = new AutorunBackupRecord(entry with { Fingerprint = changed.Fingerprint }, changed, new(false, false, ""));
    backups.Save(second);
    Check(backups.Load(entry.Id) == second && backups.ReadAll().Count == 1, "Framework atomic backup replacement retains exact latest restorable state for the same native identity");
    var avd = await new AvdRestartService().DiscoverAsync([]);
    Check(avd.Plans.Count == 0 && avd.Notices.Count > 0, "32-bit AVD restart capability is explicitly unavailable before reading or changing any emulator");
    var browser = await new ChromiumLiveService().DiscoverAsync([]);
    Check(browser.Targets.Count == 0 && browser.Notices.Count > 0, "unsupported browser preview returns no fake page or success");
    var capture = new ProcessCollector().Capture();
    using var current = Process.GetCurrentProcess();
    var me = capture.Processes.SingleOrDefault(item => item.Id == current.Id);
    Check(me is not null && me.StartTimeUtcTicks > 0 && me.Path.Length > 0 && me.OwnerSid.Length > 0, "real x86 read-only collection captures exact current-process identity");
    Check(capture.Processes.Count > 0, "x86 collector returns a process snapshot without native entry-point failure on this host");
    if (args.Contains("--read-only-live"))
    {
        var watch = Stopwatch.StartNew(); var startup = new AutorunCatalog(Path.Combine(fixtureRoot, "empty-catalog-backups")).Scan();
        Check(startup.Entries.Count <= AutorunCatalog.MaximumEntries && startup.Entries.Where(item => item.IsSystem).All(item => !item.CanChange), "x86 real startup inventory respects bounds and system read-only protection");
        Console.WriteLine("LIVE COUNTS ONLY: " + string.Join(" | ", startup.Entries.GroupBy(item => item.SourceKind).Select(group => group.Key + "=" + group.Count())));
        Console.WriteLine($"LIVE: entries={startup.Entries.Count}; warnings={startup.Warnings.Count}; truncated={startup.Truncated}; seconds={watch.Elapsed.TotalSeconds:F2}");
    }
    var ownPolicy = new ProtectionPolicy(current.SessionId, WindowsIdentity.GetCurrent().User!.Value, current.Id);
    var close = await new ProcessCloser(capture: () => capture).CloseDetailedAsync([me!], true, item => ownPolicy.Evaluate(item, capture, []), capture);
    Check(close.Results.All(item => !item.Success) && !current.HasExited, "close pipeline refuses its own verified process before any close side effect");
    L.Language = "en";
    Check(L.T("此兼容版本不读取打包应用启动项。").StartsWith("Packaged startup"), "legacy capability limits are localized in English");
    L.Language = "zh-Hant";
    Check(L.T("此兼容版本不读取打包应用启动项。").Contains("讀取"), "legacy capability limits are localized in Traditional Chinese");
}
finally { Directory.Delete(fixtureRoot, true); }
Console.WriteLine($"PASS: {count} Legacy Core assertions | x86 .NET Framework {Environment.Version} | host only, not a Windows 7 test.");

internal static class ArgumentParser
{
    internal static string[] Read(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException();
        try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(pointer); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
}
