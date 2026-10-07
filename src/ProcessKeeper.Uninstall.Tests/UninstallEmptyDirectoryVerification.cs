using ProcessKeeper.Core;

internal static class UninstallEmptyDirectoryVerification
{
    internal static async Task Run(Action<bool, string> check)
    {
        Roles(check);
        ProtectedRoots(check);
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" })
        {
            L.Language = language;
            foreach (var mode in new[] { UninstallMode.Normal, UninstallMode.RegisteredQuiet })
                check(UninstallPresentation.Confirmation(new() { Name = "Example" }, mode).Contains(L.T("卸载完成并核实注册项已移除后，只会清理已确认归属且为空的安装目录；其他文件和上级目录会保留。")), "single and quiet confirmation disclose safe empty-leaf cleanup: " + language);
        }
        L.Language = "en";
        await Directories(check);
    }

    private static void ProtectedRoots(Action<bool, string> check)
    {
        var cleanup = new UninstallEmptyDirectoryCleanup();
        foreach (var path in new[] { @"E:\", @"\\server\share\Example", @"E:\Windows\Example", @"E:\Users\Example", @"E:\Program Files", @"E:\Common Files\Example", @"E:\Temp", @"E:\Example\..\Other", @"E:\Example.", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            int scans = 0;
            using var lease = cleanup.Capture(new() { Name = "Example", InstallLocation = path, CanUninstall = true }, () => { scans++; return new(); });
            check(lease is null && scans == 0, "protected/remote/ambiguous root rejected before inventory or filesystem access: " + path);
        }
    }

    private static void Roles(Action<bool, string> check)
    {
        const string root = @"E:\NotCreatedUninstallRoleFixture\Example";
        var app = new InstalledApplication { Name = "Example", InstallLocation = root };
        foreach (var name in new[] { "unins123.exe", "unwise.exe", "unwise32.exe", "IsUninst.exe", "RemoveExample.exe", "Example_uninstall.exe", "卸载Example.exe" })
        {
            var file = new InstalledExecutable { Path = root + "\\" + name, Name = name };
            var role = new InstalledExecutableIdentity().Classify(app with { Executables = new[] { file } }, file);
            check(role.Role == InstalledExecutableRole.PotentialUninstaller && role.IsUncertain, "display-only removal convention remains uncertain: " + name);
        }
        foreach (var name in new[] { "setup.exe", "Update.exe", "MaintenanceTool.exe", "ExampleManager.exe" })
        {
            var file = new InstalledExecutable { Path = root + "\\" + name, Name = name };
            var registration = new UninstallEntry { Command = new(file.Path, "--uninstall", false) };
            var role = new InstalledExecutableIdentity(new[] { registration }).Classify(app with { Executables = new[] { file } }, file);
            check(role.Role == InstalledExecutableRole.Uninstaller && !role.IsUncertain && role.Evidence == InstalledExecutableEvidence.RegisteredUninstaller,
                "authoritative registered nonstandard route: " + name);
            var guessed = new InstalledExecutableIdentity().Classify(app with { Executables = new[] { file } }, file);
            check(guessed.Role != InstalledExecutableRole.Uninstaller && guessed.Role != InstalledExecutableRole.PotentialUninstaller,
                "unregistered updater/setup/maintenance is not a removal route: " + name);
        }
        foreach (var name in new[] { "UninstallUpdater.exe", "UninstallService.exe", "RemoveService.exe", "uninstalled-monitor.exe" })
        {
            var file = new InstalledExecutable { Path = root + "\\" + name, Name = name };
            var role = new InstalledExecutableIdentity().Classify(app with { Executables = new[] { file } }, file);
            check(role.Role != InstalledExecutableRole.Uninstaller && role.Role != InstalledExecutableRole.PotentialUninstaller, "service/updater or incidental string is not a removal convention: " + name);
        }
    }

    private static async Task Directories(Action<bool, string> check)
    {
        var configured = Environment.GetEnvironmentVariable("PROCESSKEEPER_UNINSTALL_TEST_ROOT") ?? Path.GetTempPath();
        var fixtureRoot = Path.GetFullPath(configured);
        if (fixtureRoot.Length > Path.GetPathRoot(fixtureRoot)!.Length) fixtureRoot = fixtureRoot.TrimEnd(Path.DirectorySeparatorChar);
        for (var at = fixtureRoot; !string.IsNullOrEmpty(at); at = Path.GetDirectoryName(at))
            if (Directory.Exists(at) && (File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0) throw new IOException("Uninstall fixture storage must not use a redirected route.");
        Directory.CreateDirectory(fixtureRoot);
        foreach (var mode in new[] { "success", "still", "nonzero", "monitor", "notstarted", "read-failed", "scan-failed", "scan-warning", "scan-truncated", "before-warning", "no-location", "wrong-main", "shared", "shared-child", "shared-command", "shared-ancestor", "shared-alias", "shared-forward-slash", "shared-dot-segment", "shared-unknown-route", "shared-trailing-dot", "shared-trailing-space", "shared-short-name", "shared-command-dot", "shared-command-space", "shared-command-short", "vendor-parent", "vendor-main", "vendor-suffix", "nonempty-file", "nonempty-child", "swap", "vendor-removed", "reparse", "parent-reparse", "late-shared", "late-file", "quiet", "cancel-after-scan", "protection-after-scan", "cancel-before-execution" })
        {
            var storage = Path.GetFullPath(Path.Combine(fixtureRoot, "ProcessKeeper-uninstall-empty-" + Guid.NewGuid().ToString("N")));
            check(storage.StartsWith(fixtureRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "empty directory fixture stays inside explicitly configured storage");
            var root = Path.Combine(storage, mode == "vendor-suffix" ? "Acme" : mode is "vendor-parent" or "vendor-main" ? "Vendor" : "Example");
            var original = root + "-original";
            var foreign = Path.Combine(storage, "foreign");
            Directory.CreateDirectory(root);
            var command = Path.Combine(root, "uninstall.exe"); var main = Path.Combine(root, "Example.exe");
            File.WriteAllText(command, "owned inert fixture"); File.WriteAllText(main, "owned inert fixture");
            var entry = new UninstallEntry
            {
                Id = "example", Name = "Example", InstallLocation = mode == "no-location" ? "" : root,
                Locator = new("HKCU", 32, "Example"), Fingerprint = "owned-registry", ExecutableIdentity = "owned-file", QuietExecutableIdentity = "owned-file",
                Command = new(command, "", false), QuietCommand = new(command, "/quiet", false), CanUninstall = true, CanQuietUninstall = true,
                ReviewedExecutableSha256 = new string('A', 64), ReviewedMode = mode == "quiet" ? UninstallMode.RegisteredQuiet : UninstallMode.Normal,
                ApplicationIdentity = new() { ExecutablePath = main, Status = UninstallApplicationIdentityStatus.Resolved, MatchedFileName = true }, ApplicationPaths = new[] { main }
            };
            if (mode is "wrong-main" or "vendor-parent") entry = entry with { ApplicationIdentity = null, ApplicationPaths = Array.Empty<string>(), Command = new(Path.Combine(storage, "external", "uninstall.exe"), "", false), QuietCommand = new(Path.Combine(storage, "external", "uninstall.exe"), "/quiet", false) };
            if (mode == "vendor-suffix") entry = entry with { Name = "Widget Acme", Publisher = "Acme", ApplicationIdentity = null, ApplicationPaths = Array.Empty<string>() };
            var backend = new Backend(entry, mode);
            using var cancel = new CancellationTokenSource();
            bool protectedNow = false;
            if (mode == "cancel-after-scan") backend.AfterScan = () => { if (backend.Executions > 0) cancel.Cancel(); };
            if (mode == "protection-after-scan") backend.AfterScan = () => protectedNow = true;
            if (mode == "cancel-before-execution") backend.AfterScan = () => cancel.Cancel();
            var junction = false; var parentJunction = false; var sharedAlias = false;
            try
            {
                backend.Execute = () =>
                {
                    File.Delete(command); File.Delete(main);
                    if (mode is "nonempty-file" or "late-file") File.WriteAllText(Path.Combine(root, "unknown.txt"), "preserve");
                    if (mode == "nonempty-child") Directory.CreateDirectory(Path.Combine(root, "unknown-child"));
                    if (mode == "swap") { Directory.Move(root, original); Directory.CreateDirectory(root); }
                    if (mode == "vendor-removed") Directory.Delete(root, false);
                };
                if (mode is "reparse" or "parent-reparse")
                {
                    Directory.CreateDirectory(foreign);
                    File.Move(command, Path.Combine(foreign, "uninstall.exe")); File.Move(main, Path.Combine(foreign, "Example.exe")); Directory.Delete(root, false);
                    var link = mode == "reparse" ? root : Path.Combine(storage, "route");
                    Junction(link, foreign); junction = mode == "reparse"; parentJunction = mode == "parent-reparse";
                    if (parentJunction)
                    {
                        Directory.CreateDirectory(Path.Combine(foreign, "Example"));
                        File.Move(Path.Combine(foreign, "uninstall.exe"), Path.Combine(foreign, "Example", "uninstall.exe"));
                        File.Move(Path.Combine(foreign, "Example.exe"), Path.Combine(foreign, "Example", "Example.exe"));
                        root = Path.Combine(link, "Example"); entry = entry with { InstallLocation = root, Command = new(Path.Combine(root, "uninstall.exe"), "", false), QuietCommand = new(Path.Combine(root, "uninstall.exe"), "/quiet", false),
                            ApplicationIdentity = entry.ApplicationIdentity! with { ExecutablePath = Path.Combine(root, "Example.exe") }, ApplicationPaths = new[] { Path.Combine(root, "Example.exe") } };
                        backend.Entry = entry;
                    }
                    backend.Execute = () => { };
                }
                if (mode == "shared-alias") { Junction(Path.Combine(storage, "alias"), root); sharedAlias = true; }
                UninstallResult result;
                try { result = await new UninstallManager(backend).RunAsync(entry, entry.ReviewedMode!.Value, cancel.Token, _ => !protectedNow); }
                catch (OperationCanceledException) when (mode == "cancel-before-execution") { result = new(UninstallOutcome.NotStarted, "owned cancellation"); }
                bool shouldRemove = mode is "success" or "quiet";
                bool removed = typeof(UninstallResult).GetProperty("EmptyDirectoryRemoved")?.GetValue(result) is true;
                check(removed == shouldRemove, "cleanup reporting follows confirmed ownership and success: " + mode);
                check(Directory.Exists(root) == !(shouldRemove || mode == "vendor-removed"), "only the original verified empty leaf may disappear: " + mode);
                check(Directory.Exists(storage), "cleanup never deletes the vendor/fixture parent: " + mode);
                if (mode == "swap") check(Directory.Exists(original), "replacement and renamed original are retained");
                if (mode is "nonempty-file" or "late-file") check(File.ReadAllText(Path.Combine(root, "unknown.txt")) == "preserve", "unknown file is never cleaned");
                if (mode == "nonempty-child") check(Directory.Exists(Path.Combine(root, "unknown-child")), "empty child is never recursively cleaned");
                if (mode is "reparse" or "parent-reparse") check(File.Exists(Path.Combine(foreign, parentJunction ? @"Example\Example.exe" : "Example.exe")), "junction target files are preserved");
                check(backend.Executions == (mode is "protection-after-scan" or "cancel-before-execution" ? 0 : 1), "execution rechecks late protection/cancellation: " + mode);
            }
            finally
            {
                if (junction) Directory.Delete(root, false);
                if (parentJunction) Directory.Delete(Path.Combine(storage, "route"), false);
                if (sharedAlias) Directory.Delete(Path.Combine(storage, "alias"), false);
                // This tree was created by this fixture; no production registration or files are used.
                Directory.Delete(storage, true);
            }
        }
    }

    private static void Junction(string path, string target)
    {
        var shell = Environment.GetEnvironmentVariable("PROCESSKEEPER_PWSH") ?? "pwsh";
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell)
        { UseShellExecute = false, CreateNoWindow = true, Arguments = "-NoProfile -Command \"New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null\"" })!;
        if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new IOException("Owned junction fixture could not be created.");
    }

    private sealed class Backend(UninstallEntry entry, string mode) : IUninstallBackend
    {
        public UninstallEntry Entry = entry; public Action Execute = () => { }; public Action AfterScan = () => { }; public int Executions;
        public UninstallSnapshot Scan(CancellationToken token)
        {
            if (mode == "scan-failed" && Executions > 0) throw new IOException("Owned unavailable inventory");
            var entries = new List<UninstallEntry>();
            if (Executions == 0 || mode == "still") entries.Add(Entry);
            if (mode is "shared" or "shared-child" or "shared-command" or "shared-ancestor" or "shared-alias" or "shared-forward-slash" or "shared-dot-segment" or "shared-unknown-route" or "shared-trailing-dot" or "shared-trailing-space" or "shared-short-name" or "shared-command-dot" or "shared-command-space" or "shared-command-short" || mode == "late-shared" && Executions > 0)
            {
                var location = mode == "shared-child" ? Path.Combine(Entry.InstallLocation, "Child") : mode == "shared-command" ? "" : mode == "shared-ancestor" ? Path.GetDirectoryName(Entry.InstallLocation)! : mode == "shared-alias" ? Path.Combine(Path.GetDirectoryName(Entry.InstallLocation)!, "alias") : Entry.InstallLocation;
                if (mode == "shared-forward-slash") location = location.Replace('\\', '/');
                if (mode == "shared-dot-segment") location = Path.Combine(Path.GetDirectoryName(Entry.InstallLocation)!, @"Other\..\Example");
                if (mode == "shared-unknown-route") location = @"%UNRESOLVED_FIXTURE_OWNER%\Example";
                if (mode == "shared-trailing-dot") location += ".";
                if (mode == "shared-trailing-space") location += " ";
                if (mode == "shared-short-name") location = Path.Combine(Path.GetDirectoryName(Entry.InstallLocation)!, "EXAMPL~1");
                var indirect = mode is "shared-alias" or "shared-forward-slash" or "shared-dot-segment" or "shared-unknown-route" or "shared-trailing-dot" or "shared-trailing-space" or "shared-short-name";
                var other = Entry with { Id = "other", Locator = new("HKCU", 32, "other"), Fingerprint = "other-registry", InstallLocation = location,
                    Command = indirect ? new(Path.Combine(Path.GetDirectoryName(Entry.InstallLocation)!, "external", "other.exe"), "", false) : Entry.Command, QuietCommand = indirect ? null : Entry.QuietCommand,
                    IconPath = "", ApplicationPaths = Array.Empty<string>(), ApplicationIdentity = null };
                if (mode is "shared-command-dot" or "shared-command-space" or "shared-command-short")
                {
                    var path = mode == "shared-command-short" ? Path.Combine(Path.GetDirectoryName(Entry.InstallLocation)!, @"EXAMPL~1\other.exe") : Path.Combine(Entry.InstallLocation, "other.exe") + (mode == "shared-command-dot" ? "." : " ");
                    other = other with { InstallLocation = "", Command = new(path, "", false), QuietCommand = null };
                }
                entries.Add(other);
            }
            AfterScan(); return new() { Entries = entries, Truncated = mode == "scan-truncated" && Executions > 0,
                Warnings = mode == "scan-warning" && Executions > 0 || mode == "before-warning" ? new[] { "owned incomplete inventory" } : Array.Empty<string>() };
        }
        public UninstallEntry? Read(UninstallLocator locator, CancellationToken token)
        {
            if (Executions > 0 && mode == "read-failed") throw new IOException("Owned verification unavailable");
            return Executions == 0 || mode == "still" ? Entry : null;
        }
        public UninstallReview Review(UninstallEntry expected, UninstallMode requested, CancellationToken token) => new(expected, "owned fixture");
        public Task<UninstallExecution> ExecuteAsync(UninstallEntry expected, UninstallMode requested, CancellationToken token)
        {
            Executions++; Execute();
            return Task.FromResult(mode == "notstarted" ? new UninstallExecution(false, false, null) : mode == "monitor" ? new(true, false, null, true) : new(true, true, mode == "nonzero" ? 3010 : 0));
        }
    }
}
