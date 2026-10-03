using System.Diagnostics;
using ProcessKeeper.Core;

internal static class InstalledSearchVerification
{
    internal static void Run(Action<bool, string> check, Action<string> report)
    {
        var originalLanguage = L.Language;
        try
        {
            L.Language = "en";
            check(L.T("所有盘符") == "All drives" && L.T("按盘符筛选") == "Filter by drive", "drive filter labels are covered by English localization");
            L.Language = "zh-Hant";
            check(L.T("按盘符筛选") == "按盤符篩選", "drive filter labels are covered by Traditional Chinese localization");
        }
        finally { L.Language = originalLanguage; }
        InstalledExecutable Exe(string path) => new() { Path = path, Name = Path.GetFileName(path) };
        var alpha = new InstalledApplication
        {
            Id = "alpha", Name = "Contoso Editor", Publisher = "Contoso", InstallLocation = @"C:\Apps\Editor",
            Executables = [Exe(@"C:\Apps\Editor\edit.exe"), Exe(@"C:\Apps\Editor\helper.exe")]
        };
        var duplicate = alpha with { Id = "other", Name = "Other Editor", InstallLocation = @"D:\Apps\Editor", Executables = [Exe(@"D:\Apps\Editor\helper.exe")] };
        var crossDrive = alpha with { Id = "cross", Name = "Cross drive", InstallLocation = @"E:\Apps\Cross", Executables = [Exe(@"F:\Tools\cross.exe")] };
        var noDrive = new InstalledApplication { Id = "package", Name = "Package", ApplicationKey = "package:sample" };
        var processes = new[]
        {
            new ProcessRecord { Id = 51235, Name = "helper.exe", Path = @"C:\Apps\Editor\helper.exe", SessionId = 1, Description = "Preview renderer" },
            new ProcessRecord { Id = 76431, Name = "edit.exe", Path = @"C:\Apps\Editor\edit.exe", SessionId = 1 },
            new ProcessRecord { Id = 93001, Name = "helper.exe", Path = @"D:\Apps\Editor\helper.exe", SessionId = 0, IsSystem = true }
        };
        var visible = new InstalledApplicationSearch(processes, false);
        var all = new InstalledApplicationSearch(processes, true);
        check(visible.Matches(alpha, "51235"), "process PID query retains its owning installed application");
        check(visible.MatchesComponent(alpha.Executables[1], "51235"), "PID query identifies the exact component for automatic expansion");
        check(!visible.MatchesComponent(alpha.Executables[0], "51235"), "PID query does not mark an unrelated component");
        check(!visible.Matches(duplicate, "51235"), "same executable basename in another installation does not claim a process");
        check(visible.Matches(alpha, "preview renderer"), "process description query finds the owning application");
        check(visible.Matches(alpha, "HELPER.EXE"), "executable search is case insensitive");
        check(visible.Matches(alpha, "contoso"), "application and publisher search remains available");
        check(visible.Matches(alpha, @"C:\Apps\Editor\edit.exe"), "full executable path search remains available");
        check(!visible.Matches(alpha, "5123"), "PID search requires the complete numeric process identifier");
        check(!visible.Matches(duplicate, "93001") && all.Matches(duplicate, "93001"), "system PID queries respect the explicit system-process visibility option");
        check(new InstalledApplicationSearch(new[] { processes[2] with { IsSystem = false } }, false).Matches(duplicate, "93001"), "a third-party session-zero component remains visible without verified Windows identity");
        check(visible.ProcessesFor(alpha.Executables[1]).Single().Id == 51235, "association returns the exact running process to show under its software");
        check(new InstalledApplicationSearch([], false).Matches(alpha, "helper.exe"), "non-running installed components remain searchable");
        check(InstalledApplicationSearch.DriveOf(@"c:\Apps\Editor") == "C:", "drive extraction normalizes letter case");
        check(InstalledApplicationSearch.DriveOf("D:/Apps/Editor") == "D:", "forward slash absolute paths retain their drive");
        check(InstalledApplicationSearch.DriveOf(@"C:relative.exe") == "", "drive-relative paths do not invent an installed drive");
        check(InstalledApplicationSearch.DriveOf(@"\\server\share\tool.exe") == "", "network paths do not invent a local drive filter");
        check(InstalledApplicationSearch.Drives([alpha, duplicate, crossDrive, noDrive]).SequenceEqual(["C:", "D:", "E:", "F:"]), "drive choices include distinct installation roots and executable locations only");
        check(visible.Matches(noDrive, "", ""), "default all-drives choice includes installations with an unknown drive");
        check(!visible.Matches(noDrive, "", "C:"), "specific-drive filter excludes installations without a matching known path");
        check(visible.Matches(crossDrive, "", "F:") && visible.Matches(crossDrive, "", "E:"), "split installations match either their install drive or component drive");
        check(!visible.Matches(alpha, "51235", "D:") && visible.Matches(alpha, "51235", "C:"), "drive and process queries are combined without losing ownership");

        // The upper catalog bound is deliberately larger than an ordinary machine's list.
        // One shared process index is built per query rather than per installed component.
        var many = Enumerable.Range(0, InstalledApplicationCatalog.MaximumApplications).Select(i => new InstalledApplication
        {
            Id = i.ToString(), Name = "Fixture " + i, InstallLocation = @"E:\Fixtures\" + i,
            Executables = Enumerable.Range(0, 8).Select(j => Exe($@"E:\Fixtures\{i}\part{j}.exe")).ToArray()
        }).ToArray();
        var target = new ProcessRecord { Id = 91919, SessionId = 1, Name = "part7.exe", Path = many[^1].Executables[^1].Path };
        var indexed = new InstalledApplicationSearch([target], false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        var found = many.Where(a => indexed.Matches(a, "91919", "E:")).ToArray();
        clock.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        check(found.Length == 1 && found[0].Id == many[^1].Id, "2,500 applications / 20,000 components retain the correct parent for a PID search");
        check(allocated < 32L * 1024 * 1024, "large catalog query does not allocate an application-by-process cross product");
        report($"QUERY BENCHMARK: {many.Length} applications / {many.Sum(a => a.Executables.Count)} components; {clock.Elapsed.TotalMilliseconds:F2} ms; {allocated:N0} bytes allocated.");
    }
}
