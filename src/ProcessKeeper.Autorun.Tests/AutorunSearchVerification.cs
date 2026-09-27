using ProcessKeeper.Core;

internal static class AutorunSearchVerification
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("Autorun search: " + name); count++; Console.WriteLine("PASS | autorun search | " + name); }
        var process = new ProcessRecord { Id = 73421, Name = "helper.exe", Path = @"D:\Fixture\One\helper.exe", ApplicationName = "Fixture Editor", ApplicationKey = "fixture:one" };
        var other = process with { Id = 81234, Path = @"E:\Fixture\Two\helper.exe", ApplicationName = "Unrelated Viewer", ApplicationKey = "fixture:two" };
        var shell = new ProcessRecord { Id = 90001, Name = "powershell.exe", Path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ApplicationName = "Other automation" };
        var service = new ProcessRecord { Id = 90002, Name = "svchost.exe", Path = @"C:\Windows\System32\svchost.exe", ApplicationName = "Service Host", Services = [new("FixtureService", "Fixture service", 90002)] };
        var index = new AutorunSearchIndex(new(DateTimeOffset.UtcNow, [process, other, shell, service], []),
            [new() { Id = "offline", Name = "Offline Product", Executables = [new() { Path = @"F:\Offline\agent.exe" }] }]);
        var entry = new AutorunEntry { Id = "entry", Name = "Unrelated registry label", SourceKind = AutorunSourceKind.RegistryRun, TargetPath = @"d:\fixture\one\helper.exe", Location = @"HKCU\Run\Fixture", Command = @"D:\Fixture\One\helper.exe --background" };
        Check(index.Processes(entry).Count == 1 && index.Processes(entry)[0].Id == 73421, "case-insensitive exact executable path");
        Check(index.Matches(entry, "Fixture Editor"), "owning software name finds its startup entry");
        Check(index.Matches(entry, "helper.exe"), "process filename finds its owner");
        Check(index.Matches(entry, "73421"), "exact process PID finds its owner");
        Check(!index.Matches(entry, "7342"), "partial PID is not treated as exact process identity");
        Check(!index.Matches(entry, "Unrelated Viewer"), "same filename elsewhere does not cross-associate");
        Check(!index.Matches(entry, "81234"), "other installation PID does not cross-associate");
        Check(index.Matches(entry, "--background"), "startup arguments remain searchable");
        Check(index.Matches(entry, "HKCU"), "source location remains searchable");
        Check(index.Matches(entry, ""), "empty search retains the entry");
        var hosted = entry with { TargetPath = shell.Path, Command = "unrelated-script", Name = "Automation" };
        Check(index.Processes(hosted).Count == 0, "shared PowerShell host is not attributed by executable path");
        Check(!index.Matches(hosted, "90001"), "shared host PID is not claimed by unrelated entry");
        var serviceEntry = entry with { SourceKind = AutorunSourceKind.Service, ServiceName = "fixtureservice", TargetPath = service.Path };
        Check(index.Processes(serviceEntry).Single().Id == service.Id, "service identity can link a shared host correctly");
        Check(index.Processes(serviceEntry with { ServiceName = "UnrelatedService" }).Count == 0, "different service cannot claim same shared host");
        Check(index.Processes(entry with { TargetPath = service.Path }).Count == 0, "non-service registry entry cannot claim svchost by path");
        var offline = entry with { TargetPath = @"f:\offline\agent.exe" };
        Check(index.Matches(offline, "Offline Product"), "offline installed software name remains searchable");
        Check(index.Processes(offline).Count == 0, "offline executable never invents a process");
        Check(index.Applications(offline).Single() == "Offline Product", "offline owner is available for display");
        Check(index.Processes(entry with { TargetPath = "helper.exe" }).Count == 0, "relative paths do not claim full-path identity");
        Check(index.Processes(entry with { TargetPath = @"D:\Fixture\OneMore\helper.exe" }).Count == 0, "directory prefix is not an executable identity");
        var many = Enumerable.Range(0, 2500).Select(i => entry with { Id = "entry" + i }).ToArray();
        Check(many.Count(e => index.Matches(e, "73421")) == 2500, "bounded index reuses process associations across a large inventory");
        Check(many.All(e => index.Matches(e, "73421") && !index.Matches(e, "81234")), "successive searches do not contaminate one another");
        return count;
    }
}
