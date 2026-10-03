using System.Diagnostics;
using System.Reflection;
using ProcessKeeper.Core;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--owned-helper") { await Task.Delay(1200); return 23; }
        try
        {
            L.Language = "en";
            var command = UninstallPolicy.Parse("\"E:\\Apps\\Example\\setup.exe\" /remove", false, "demo", @"C:\Windows\System32");
            Check(command is { IsMsi: false } && command.Executable == @"E:\Apps\Example\setup.exe", "native vendor setup accepted");
            foreach (var raw in new[] { "cmd.exe /c remove", "\"C:\\Windows\\System32\\cmd.exe\" /c remove", "\\\\server\\app\\uninstall.exe", "E:\\Apps Folder\\uninstall.exe /S", "E:\\Apps\\..\\uninstall.exe", "E:\\Apps\\uninstall.cmd", "\"E:\\Apps\\uninstall.exe\"/S", "E:\\Apps\\uninstall.exe & evil", "E:\\Apps\\uninstall.exe\n/S", "E:\\Apps\\uninstall.exe:stream" })
                Check(UninstallPolicy.Parse(raw, false, "demo", @"C:\Windows\System32") is null, "reject ambiguous or shell route " + raw);
            var product = "{12345678-1234-1234-1234-123456789ABC}";
            var msi = UninstallPolicy.Parse("bad registry command", true, product, @"C:\Windows\System32");
            Check(msi is { IsMsi: true } && msi.Arguments == "/x " + product + " /norestart", "MSI canonical product route");
            Check(UninstallPolicy.Parse("msiexec /i anything", true, "bad", @"C:\Windows\System32") is null, "MSI invalid product refused");
            foreach (var name in new[] { "360安全卫士", "360 Total Security", "金山毒霸", "Kingsoft Antivirus 2026" }) Check(UninstallPolicy.IsRecommended(name), "watchlist exact product " + name);
            foreach (var name in new[] { "360 degree editor", "WPS Office", "Kingsoft Office", "My 360安全卫士 notes", "Windows Security" }) Check(!UninstallPolicy.IsRecommended(name), "not broad malware verdict " + name);
            var entry = Entry();
            Check(UninstallPresentation.Matches(entry, "setup.exe") && UninstallPresentation.Matches(entry, "Example") && UninstallPresentation.Matches(entry, "HKCU"), "search process and owner and registration");
            Check(!UninstallPolicy.Same(entry, entry with { Fingerprint = "changed" }), "registry changes refused");
            Check(!UninstallPolicy.Same(entry, entry with { ExecutableIdentity = "changed" }), "file changes refused");
            RegistryMapping();
            UninstallApplicationIdentityVerification.Run(Check);
            DisplayCases();
            Recommendations();
            await ManagerCases(entry);
            await BatchCases();
            await SelectedBatchCases();
            await OwnProcessObservation();
            Console.WriteLine($"PASS | {_checks} uninstall checks | {Environment.Version} | process {(IntPtr.Size * 8)}-bit");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static UninstallEntry Entry() => new()
    {
        Id = "example", Name = "Example app", Publisher = "Example team", Locator = new("HKCU", 64, "Example"),
        Fingerprint = "registry", ExecutableIdentity = "native-file", QuietExecutableIdentity = "native-file",
        Command = new(@"E:\Apps\Example\setup.exe", "/remove", false), QuietCommand = new(@"E:\Apps\Example\setup.exe", "/remove /quiet", false),
        CanUninstall = true, CanQuietUninstall = true, ReviewedExecutableSha256 = new string('A', 64), ReviewedMode = UninstallMode.Normal
    };
    private static UninstallEntry Map(Dictionary<string, object?> values, UninstallLocator? locator = null, Func<string, string>? identity = null)
    {
        var method = typeof(UninstallWindowsBackend).GetMethod("FromValues", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (UninstallEntry)method.Invoke(null, new object[] { locator ?? new("HKCU", 64, "Example"), values, @"C:\Windows\System32", identity ?? (_ => "file") })!;
    }
    private static void RegistryMapping()
    {
        var values = new Dictionary<string, object?> { ["DisplayName"] = new object?[] { 1u, "Example app" }, ["UninstallString"] = new object?[] { 1u, "E:\\Apps\\setup.exe /remove" }, ["NoRemove"] = new object?[] { 4u, 1 } };
        var hidden = Map(values);
        Check(hidden.IsHidden && hidden.RegistrationNoRemove && hidden.CanUninstall && !hidden.IsSystem, "NoRemove stays manageable hidden entry");
        values["SystemComponent"] = new object?[] { 4u, 1 };
        Check(Map(values).CanUninstall, "hidden SystemComponent flag alone does not prohibit vendor uninstall");
        values["ParentKeyName"] = new object?[] { 1u, "Parent" };
        Check(Map(values).IsSystem && !Map(values).CanUninstall, "child component read only");
        values.Remove("ParentKeyName"); values["ReleaseType"] = new object?[] { 1u, "Security Update" };
        Check(Map(values).IsSystem && !Map(values).CanUninstall, "OS update read only");
        values.Remove("ReleaseType");
        Check(!Map(values, identity: _ => "").CanUninstall, "missing executable read only");
        var ids = new HashSet<string>();
        foreach (var hive in new[] { "HKLM", "HKCU" }) foreach (var view in new[] { 32, 64 })
        { var row = Map(values, new(hive, view, "Example")); Check(row.Locator.Hive == hive && row.Locator.View == view && ids.Add(row.Id), "registration hive/view stable independent identity"); }
        values.Remove("DisplayName");
        Check(Map(values).IsHidden && !Map(values).CanUninstall && Map(values).Name == "Example", "unnamed hidden registration inspectable");
        Check(!Map(values).HasDisplayName && !UninstallDisplay.IsSimpleVisible(Map(values)), "unnamed registration is excluded from simple mode");
    }
    private static void DisplayCases()
    {
        var entry = Entry();
        Check(UninstallDisplay.IsSimpleVisible(entry) && UninstallDisplay.MatchesFilter(entry, false, 0), "simple mode includes a verified native uninstall route");
        foreach (var row in new[] { entry with { Command = null }, entry with { ExecutableIdentity = "" },
            entry with { Name = @"HKEY_LOCAL_MACHINE\SOFTWARE\Example" }, entry with { Name = @"HKCU\Software\Example" },
            entry with { Name = @"E:\Apps\setup.exe" }, entry with { Name = "{12345678-1234-1234-1234-123456789ABC}" },
            entry with { HasDisplayName = false } })
            Check(!UninstallDisplay.IsSimpleVisible(row) && UninstallDisplay.MatchesFilter(row, true, 0), "complex view preserves registration-only or missing-entry diagnostics");
        Check(UninstallDisplay.IsSimpleVisible(entry with { IsSystem = true, CanUninstall = false }), "read-only system entry with real route is visible without granting uninstall");
        Check(UninstallDisplay.Classify(entry) == UninstallAppCategory.ThirdPartyApplication, "ordinary application remains third-party");
        var microsoft = entry with { Publisher = "Microsoft Corporation" };
        var runtime = microsoft with { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)" };
        Check(UninstallDisplay.Classify(runtime) == UninstallAppCategory.Runtime && UninstallDisplay.MatchesFilter(runtime, false, 3)
            && !UninstallDisplay.MatchesFilter(runtime, false, 1), "VC++ is runtime not a driver or ordinary Microsoft application");
        Check(UninstallDisplay.Classify(microsoft with { Name = "Microsoft .NET 8.0.8 - Windows Server Hosting" }) == UninstallAppCategory.Runtime,
            "Microsoft hosting bundle belongs to runtimes");
        Check(UninstallDisplay.Classify(microsoft with { Name = "Microsoft Edge" }) == UninstallAppCategory.ThirdPartyApplication, "Microsoft registry labels alone do not establish system ownership");
        Check(UninstallDisplay.Classify(runtime with { Publisher = "Unrelated Publisher" }) == UninstallAppCategory.ThirdPartyApplication, "runtime classification does not match product name alone");
        Check(UninstallDisplay.Classify(entry with { Name = "NVIDIA Graphics Driver 570.0", Publisher = "NVIDIA Corporation" }) == UninstallAppCategory.Driver, "registered graphics driver belongs to driver category");
        Check(UninstallDisplay.Classify(entry with { Name = "Driver Training Simulator", Publisher = "Example team" }) == UninstallAppCategory.ThirdPartyApplication, "ordinary driver-named app is not a hardware driver");
        foreach (int index in Enumerable.Range(0, 6))
        {
            var row = entry with { IsHidden = true, IsRecommended = true };
            bool expected = index is 0 or 2 or 3 or 4;
            Check(UninstallDisplay.MatchesFilter(row, true, index) == expected, "advanced six filters preserve prior semantics " + index);
        }
        Check(!UninstallDisplay.MatchesFilter(entry, false, 4) && !UninstallDisplay.MatchesFilter(entry, true, 6), "out of range filters return no matches");
        var oldVersion = entry with { Id = "old", Version = "1.0", Locator = new("HKLM", 32, "Old") };
        var newVersion = entry with { Id = "new", Version = "2.0", Locator = new("HKLM", 64, "New") };
        var perUser = entry with { Id = "user", Version = "2.0", Locator = new("HKCU", 64, "User") };
        var groups = UninstallDisplay.Group(new[] { oldVersion, newVersion, perUser, oldVersion });
        Check(groups.Count == 1 && groups[0].Entries.Count == 3, "same name groups every version architecture and scope without duplicate snapshots");
        Check(ReferenceEquals(groups[0].Entries[0], oldVersion) && ReferenceEquals(groups[0].Entries[1], newVersion)
            && ReferenceEquals(groups[0].Entries[2], perUser), "group children retain exact independent execution objects");
        Check(UninstallDisplay.Group(new[] { entry, entry with { Name = "Example app 2", Id = "second" } }).Count == 2,
            "different product names are never merged by removing version-like digits");
        var normalized = UninstallDisplay.Group(new[] { entry, newVersion with { Name = " ＥＸＡＭＰＬＥ   app " } });
        Check(normalized.Count == 1 && normalized[0].Entries.Count == 2, "display grouping normalizes full-width letters case and whitespace only");
        Check(normalized[0].Key == UninstallDisplay.Group(new[] { newVersion with { Name = "EXAMPLE app" } })[0].Key,
            "group key remains stable across versions and snapshot ordering");
        Check(UninstallDisplay.Group(new[] { oldVersion with { HasDisplayName = false }, newVersion with { HasDisplayName = false } }).Count == 2,
            "fallback registration names never create application groups");
        Check(UninstallDisplay.Group(new[] { oldVersion, oldVersion with { Fingerprint = "changed" } })[0].Entries.Count == 2,
            "same id with changed identity is not silently deduplicated");
        var large = Enumerable.Range(0, 10000).Select(i => entry with { Id = "entry-" + i, Locator = new("HKCU", 64, "entry-" + i) });
        Check(UninstallDisplay.Group(large).Single().Entries.Count == 10000, "large same-name inventory preserves every child without filesystem scanning");
        Check(UninstallDisplay.ResolveApplicationLocation(entry with { InstallLocation = @"\\example.invalid\share", IconPath = @"C:\Windows\System32\msiexec.exe", Command = new(@"C:\Windows\System32\msiexec.exe", "/x test", true) }) == null,
            "MSI shared host is never exposed as the application location");
        var scratch = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "ProcessKeeper-uninstall-location-" + Guid.NewGuid().ToString("N"));
        Check(Path.GetPathRoot(scratch)!.Equals("E:\\", StringComparison.OrdinalIgnoreCase), "location fixture stays on E drive");
        Directory.CreateDirectory(scratch);
        var icon = Path.Combine(scratch, "owned-icon.exe");
        try
        {
            File.WriteAllBytes(icon, Array.Empty<byte>());
            Check(UninstallDisplay.ResolveApplicationLocation(entry with { InstallLocation = scratch, IconPath = icon }) == scratch, "actual install directory has location priority");
            Check(UninstallDisplay.ResolveApplicationLocation(entry with { IconPath = icon }) == icon, "actual registered icon is a file-location fallback");
        }
        finally { File.Delete(icon); Directory.Delete(scratch, false); }
    }
    private static async Task ManagerCases(UninstallEntry entry)
    {
        var fake = new Backend(entry); var manager = new UninstallManager(fake);
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.StillRegistered, "exit zero is not uninstall success");
        fake.RemoveAfter = true;
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.RegistrationRemoved, "removed original registration reported");
        fake.RemoveAfter = false; fake.ThrowAfter = true;
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.VerificationUnavailable, "verification failure not success");
        fake.ThrowAfter = false; fake.Executed = false; fake.Current = entry with { Fingerprint = "changed" }; int calls = fake.Executions;
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted && fake.Executions == calls, "changed registration never launches");
        fake.Current = entry;
        Check((await manager.RunAsync(entry with { ReviewedExecutableSha256 = "" }, UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted, "unreviewed never launches");
        Check((await manager.RunAsync(entry, UninstallMode.RegisteredQuiet)).Outcome == UninstallOutcome.NotStarted, "normal confirmation cannot authorize quiet route");
        var quiet = entry with { ReviewedMode = UninstallMode.RegisteredQuiet };
        Check((await manager.RunAsync(quiet, UninstallMode.RegisteredQuiet)).Started && fake.LastMode == UninstallMode.RegisteredQuiet, "reviewed quiet route reaches backend");
        fake.Execution = new(true, false, null, true);
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.MonitoringStopped, "stop observation not kill or success");
        fake.Execution = new(false, false, null);
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted, "launch failure accurate");
        fake.Current = entry with { IsSystem = true, CanUninstall = false };
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted, "system component refused even fabricated review");
        fake.Current = entry; using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await manager.RunAsync(entry, UninstallMode.Normal, token: cancelled.Token); throw new Exception("prelaunch cancellation ignored"); } catch (OperationCanceledException) { Check(true, "prelaunch cancellation"); }
        fake.Execution = new(true, true, 0); fake.Wait = new TaskCompletionSource<bool>();
        var active = manager.RunAsync(entry, UninstallMode.Normal); await fake.Entered.Task;
        Check((await new UninstallManager(new Backend(entry)).RunAsync(entry, UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted, "global concurrent uninstall refused");
        fake.Wait.SetResult(true); await active;
        Check((await manager.RunAsync(entry, UninstallMode.Normal)).Started, "execution gate released");
    }
    private static async Task BatchCases()
    {
        var first = Entry() with { Id = "first", Name = "Suggested first", Locator = new("HKCU", 64, "first"), IsRecommended = true };
        var second = first with { Id = "second", Name = "Suggested second", Locator = new("HKCU", 64, "second") };
        var excluded = first with { Id = "system", Name = "Read-only system", Locator = new("HKLM", 64, "system"), IsSystem = true, CanUninstall = false };
        var ordinary = Entry() with { Name = "Auradio" };
        UninstallSnapshot Snapshot() => new() { Entries = new[] { first, second, excluded, ordinary } };
        var titles = new[] { L.T("第 1 / 3 步 | 核对卸载清单"), L.T("第 2 / 3 步 | 风险与免责声明"), L.T("第 3 / 3 步 | 最终确认卸载") };
        for (int cancelAt = 1; cancelAt <= 3; cancelAt++)
        {
            var backend = new BatchBackend(first, second); int confirmations = 0, executing = 0;
            var result = await new UninstallBatch(backend).RunAsync(Snapshot(), (title, text) =>
            {
                Check(title == titles[confirmations], "mandatory three-confirmation order"); confirmations++;
                if (confirmations == 1) Check(text.Contains(first.Name) && text.Contains(second.Name) && text.Contains(excluded.Name) && !text.Contains("Auradio"), "complete target and excluded names; normal software excluded");
                if (confirmations == 2) Check(text.Contains("not a virus verdict") && text.Contains("Ignore risks") && text.Contains("restart"), "disclaimer and risk-mode-independent confirmations");
                if (confirmations == 3) Check(backend.Reviews == 2 && text.Contains(new string('A', 64)) && text.Contains(first.Registration), "all targets and hashes reviewed before final confirmation");
                return Task.FromResult(confirmations != cancelAt);
            }, () => true, () => executing++);
            Check(confirmations == cancelAt && backend.Launches.Count == 0 && executing == 0 && result.Items.All(i => i.State == UninstallBatchState.NotStarted), "cancel step " + cancelAt + " never starts anything");
        }
        var mutable = Snapshot().Entries.ToList(); var completeBackend = new BatchBackend(first, second); int count = 0;
        var complete = await new UninstallBatch(completeBackend).RunAsync(new() { Entries = mutable }, (_, _) =>
        { count++; if (count == 1) mutable.Add(first with { Id = "late", Name = "Late addition", Locator = new("HKCU", 64, "late") }); return Task.FromResult(true); }, () => true, () => { });
        Check(count == 3 && complete.Items.Count == 2 && complete.Items.All(i => i.State == UninstallBatchState.Completed) && completeBackend.Launches.SequenceEqual(new[] { first.Id, second.Id }), "fixed plan excludes additions and launches only normal routes in order");
        Check(completeBackend.AllReviewedBeforeLaunch && completeBackend.Modes.All(m => m == UninstallMode.Normal), "pre-review entire plan and never batch quiet mode");

        foreach (var outcome in new[] { "still", "monitor", "verify", "nonzero", "notstarted" })
        {
            var backend = new BatchBackend(first, second) { Outcome = outcome };
            var result = await new UninstallBatch(backend).RunAsync(Snapshot(), (_, _) => Task.FromResult(true), () => true, () => { });
            Check(backend.Launches.Count <= 1 && result.Items[1].State == UninstallBatchState.NotStarted && result.StopReason.Length > 0, "uncertain/failed first result stops remainder " + outcome);
            Check(result.Items[0].State != UninstallBatchState.Completed, "uncertain result not counted complete " + outcome);
        }
        foreach (var change in new[] { "registry", "hash" })
        {
            var backend = new BatchBackend(first, second); int step = 0;
            var result = await new UninstallBatch(backend).RunAsync(Snapshot(), (_, _) =>
            { if (++step == 3) { if (change == "registry") backend.Current[first.Id] = first with { Fingerprint = "changed" }; else backend.Hash = new string('B', 64); } return Task.FromResult(true); }, () => true, () => { });
            Check(backend.Launches.Count == 0 && result.Items[1].State == UninstallBatchState.NotStarted, "post-confirmation identity change stops all launches " + change);
        }
        var badReview = new BatchBackend(first, second) { FailReview = second.Id }; int reviewDialogs = 0;
        await new UninstallBatch(badReview).RunAsync(Snapshot(), (_, _) => { reviewDialogs++; return Task.FromResult(true); }, () => true, () => { });
        Check(reviewDialogs == 2 && badReview.Launches.Count == 0, "any pre-review failure prevents whole batch");
        var blocked = new BatchBackend(first, second); bool allowed = true; int blockingStep = 0;
        await new UninstallBatch(blocked).RunAsync(Snapshot(), (_, _) => { if (++blockingStep == 3) allowed = false; return Task.FromResult(true); }, () => allowed, () => { });
        Check(blocked.Launches.Count == 0, "outer state rechecked after final confirmation");

        var tooMany = Enumerable.Range(0, UninstallBatch.MaximumPreviewEntries + 1).Select(i => first with { Id = "limit-" + i, Locator = new("HKCU",64,"limit-" + i), Name = new string('x', 64000) }).ToArray();
        var countBackend = new BatchBackend(tooMany); int countDialogs = 0;
        var countLimit = await new UninstallBatch(countBackend).RunAsync(new() { Entries = tooMany }, (_, _) => { countDialogs++; return Task.FromResult(true); }, () => true, () => { });
        Check(countDialogs == 0 && countBackend.Reviews == 0 && countBackend.Launches.Count == 0 && countLimit.StopReason.Contains("100") && countLimit.Details.Length < UninstallBatch.MaximumPreviewCharacters, "excess entries rejected before any giant preview; bounded rejection details");
        var textBackend = new BatchBackend(first, second) { ReviewText = new string('x', UninstallBatch.MaximumPreviewCharacters) }; int textDialogs = 0;
        var textLimit = await new UninstallBatch(textBackend).RunAsync(Snapshot(), (_, _) => { textDialogs++; return Task.FromResult(true); }, () => true, () => { });
        Check(textDialogs == 2 && textBackend.Launches.Count == 0 && textLimit.StopReason.Contains("64000") && textLimit.Items.All(i => i.State == UninstallBatchState.NotStarted), "excess final review text refuses whole batch without a truncated authorization");

        var serial = new BatchBackend(first, second) { Wait = new TaskCompletionSource<bool>() };
        var pending = new UninstallBatch(serial).RunAsync(Snapshot(), (_, _) => Task.FromResult(true), () => true, () => { });
        await serial.Entered.Task;
        Check(serial.Launches.Count == 1 && !pending.IsCompleted, "second uninstaller waits for first");
        Check((await new UninstallManager(new Backend(Entry())).RunAsync(Entry(), UninstallMode.Normal)).Outcome == UninstallOutcome.NotStarted, "execution gate held across whole batch");
        serial.Wait.SetResult(true); var serialResult = await pending;
        Check(serial.MaxActive == 1 && serialResult.Items.All(i => i.State == UninstallBatchState.Completed), "batch remains serial");
        using var cancelled = new CancellationTokenSource(); var cancelBackend = new BatchBackend(first, second) { CancelAfterLaunch = cancelled };
        var stopped = await new UninstallBatch(cancelBackend).RunAsync(Snapshot(), (_, _) => Task.FromResult(true), () => true, () => { }, cancelled.Token);
        Check(cancelBackend.Launches.Count == 1 && stopped.Items[0].State == UninstallBatchState.Unconfirmed && stopped.Items[1].State == UninstallBatchState.NotStarted, "stopping observation preserves active uninstaller and stops remainder");
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" })
        {
            L.Language = language; Check(L.T("卸载所有建议卸载的应用").Length > 0 && new UninstallBatchResult(complete.Items, "").Summary.Contains("2"), "localized batch label and summary " + language);
        }
        L.Language = "en";
    }
    private static async Task SelectedBatchCases()
    {
        var method = typeof(UninstallBatch).GetMethod("RunSelectedAsync");
        Check(method != null, "selected uninstall has a registered-route batch entry point");
        var first = Entry() with { Id = "selected-one", IsRecommended = false, Locator = new("HKCU", 64, "SelectedOne") };
        var second = first with { Id = "selected-two", Locator = new("HKCU", 64, "SelectedTwo") };
        async Task<UninstallBatchResult> Run(BatchBackend backend, Func<string, string, Task<bool>> confirm, Func<UninstallEntry, bool> allowed) =>
            await (Task<UninstallBatchResult>)method!.Invoke(new UninstallBatch(backend), new object[] { new UninstallSnapshot { Entries = new[] { first, second } }, confirm, (Func<bool>)(() => true), (Action)(() => { }), CancellationToken.None, allowed })!;
        var backend = new BatchBackend(first, second); int dialogs = 0;
        var completed = await Run(backend, (_, _) => { dialogs++; return Task.FromResult(true); }, _ => true);
        Check(dialogs == 3 && backend.Launches.SequenceEqual(new[] { first.Id, second.Id }) && completed.Items.All(item => item.State == UninstallBatchState.Completed), "two selected nonrecommended applications use three confirmations and registered ordinary uninstallers");
        bool allowedNow = true; var changed = new BatchBackend(first, second); int changedDialogs = 0;
        var stopped = await Run(changed, (_, _) => { if (++changedDialogs == 3) allowedNow = false; return Task.FromResult(true); }, _ => allowedNow);
        Check(changed.Launches.Count == 0 && stopped.Items.All(item => item.State == UninstallBatchState.NotStarted), "whitelist protection enabled during final confirmation prevents all selected launches");
        var between = new BatchBackend(first, second); bool secondAllowed = true; between.AfterLaunch = id => { if (id == first.Id) secondAllowed = false; };
        var partial = await Run(between, (_, _) => Task.FromResult(true), entry => entry.Id != second.Id || secondAllowed);
        Check(between.Launches.SequenceEqual(new[] { first.Id }) && partial.Items[1].State == UninstallBatchState.NotStarted, "whitelist protection is rechecked before each selected uninstaller");
    }
    private sealed class BatchBackend(params UninstallEntry[] entries) : IUninstallBackend
    {
        public readonly Dictionary<string, UninstallEntry> Current = entries.ToDictionary(e => e.Id);
        public readonly List<string> Launches = new(); public readonly List<UninstallMode> Modes = new();
        public int Reviews, Active, MaxActive; public bool AllReviewedBeforeLaunch = true;
        public string Outcome = "remove", Hash = new string('A', 64), FailReview = "", ReviewText = "Unsigned fixture | Review before continuing";
        public TaskCompletionSource<bool>? Wait; public TaskCompletionSource<bool> Entered = new(); public CancellationTokenSource? CancelAfterLaunch;
        public Action<string>? AfterLaunch;
        public UninstallSnapshot Scan(CancellationToken token) => throw new Exception("Batch must not rescan and enlarge its plan");
        public UninstallEntry? Read(UninstallLocator locator, CancellationToken token)
        { if (Outcome == "verify" && Launches.Count > 0) throw new IOException("Cannot verify"); return Current.Values.FirstOrDefault(e => e.Locator == locator); }
        public UninstallReview Review(UninstallEntry expected, UninstallMode mode, CancellationToken token)
        { if (expected.Id == FailReview) throw new IOException("Review failed"); Reviews++; return new(expected with { ReviewedExecutableSha256 = Hash, ReviewedMode = mode }, ReviewText); }
        public async Task<UninstallExecution> ExecuteAsync(UninstallEntry expected, UninstallMode mode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (expected.ReviewedExecutableSha256 != Hash) throw new IOException("Hash changed before launch");
            if (Outcome == "notstarted") return new(false, false, null);
            AllReviewedBeforeLaunch &= Reviews == entries.Length; Launches.Add(expected.Id); Modes.Add(mode); Active++; MaxActive = Math.Max(MaxActive, Active); Entered.TrySetResult(true);
            if (Wait is not null) await Wait.Task;
            CancelAfterLaunch?.Cancel(); Active--;
            if (Outcome == "monitor" || token.IsCancellationRequested) return new(true, false, null, true);
            if (Outcome != "still" && Outcome != "verify") Current.Remove(expected.Id);
            AfterLaunch?.Invoke(expected.Id);
            return new(true, true, Outcome == "nonzero" ? 3010 : 0);
        }
    }
    private static void Recommendations()
    {
        Check(UninstallRecommendations.Rules.Count >= 60, "versioned catalog covers domestic and international named products");
        Check(UninstallRecommendations.Rules.Select(r => r.Id).Distinct().Count() == UninstallRecommendations.Rules.Count, "catalog IDs unique");
        foreach (var rule in UninstallRecommendations.Rules)
        {
            foreach (var name in rule.Names)
            {
                Check(UninstallRecommendations.Match(name)?.Rule.Id == rule.Id, "exact name " + name);
                Check(UninstallRecommendations.Match(name + " v1.2.3")?.Rule.Id == rule.Id, "explicit version " + name);
                Check(UninstallRecommendations.Match(name + " unrelated product") is null, "unrelated suffix not classified " + name);
            }
            Check(rule.Basis == UninstallRecommendationBasis.UserWatchlist ? rule.SourceUrl.Length == 0 : (rule.SourceUrl.StartsWith("https://www.malwarebytes.com/blog/detections/", StringComparison.Ordinal) || rule.SourceUrl.StartsWith("https://www.huorong.cn/document/tech/", StringComparison.Ordinal) || rule.SourceUrl == "https://www.360.cn/n/11465.html"), "evidence kind distinct " + rule.Id);
        }
        foreach (var name in new[] { "360", "360浏览器", "360 Driver Helper", "Microsoft Edge", "压缩", "浏览器", "Google Chrome", "CCleaner", "WPS Office", "搜狗输入法", "Baidu Netdisk", "ProcessKeeper", "PuP.Optional.WebCompanion", "My Web Companion", "notes about Wajam", "7-Zip", "WinRAR", "2345", "金山词霸", "Total Commander", "commander", "云计算", "多标签文件管理器", "鲁大师 diagnostic notes", new string('x', 513) })
            Check(UninstallRecommendations.Match(name) is null, "no generic brand/category or detection-name match " + name.Substring(0, Math.Min(40, name.Length)));
        Check(UninstallRecommendations.Match("３６０安全卫士")?.Rule.Id == "360-security", "fullwidth registration normalized");
        Check(UninstallRecommendations.Match("OneStart (x64)")?.Rule.Id == "onestart", "explicit architecture suffix");
        Check(UninstallRecommendations.Match("Wajam\n") is null, "control characters rejected");
        foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" })
        {
            L.Language = language;
            var watched = UninstallRecommendations.Describe(Entry() with { Name = "金山毒霸", IsRecommended = true });
            var published = UninstallRecommendations.Describe(Entry() with { Name = "OneStart", IsRecommended = true });
            Check(watched.Contains(L.T("用户关注名单")) && !watched.Contains("https://www.malwarebytes.com"), "watchlist does not fabricate external report " + language);
            Check(published.Contains(L.T("公开风险报告")) && published.Contains("https://www.malwarebytes.com/blog/detections/pup-optional-onestart"), "report provenance presented " + language);
            Check(UninstallRecommendations.Coverage.Contains(UninstallRecommendations.Version), "coverage version visible " + language);
            Check(!UninstallPresentation.Summary(Entry() with { IsRecommended = true }).Contains(L.T("建议卸载")), "recommendation belongs to a separate badge, not summary " + language);
        }
        L.Language = "en";
    }
    private static async Task OwnProcessObservation()
    {
        // Runs only this test executable as a timed no-op. Does not touch any registration.
        var assembly = Assembly.GetExecutingAssembly().Location;
        var executable = assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")!, "dotnet.exe") : assembly;
        var arguments = (assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "\"" + assembly + "\" " : "") + "--owned-helper";
        var observe = typeof(UninstallWindowsBackend).GetMethod("ObserveAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var process = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await (Task<UninstallExecution>)observe.Invoke(null, new object[] { process, cancel.Token })!;
        Check(result.Started && result.MonitoringStopped && !process.HasExited, "actual helper remains running after observation cancelled");
        Check(process.WaitForExit(10000) && process.ExitCode == 23, "owned helper exits itself without being killed");
        using var finished = Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        var observed = await (Task<UninstallExecution>)observe.Invoke(null, new object[] { finished, CancellationToken.None })!;
        Check(observed.Exited && !observed.MonitoringStopped && observed.ExitCode == 23, "actual helper nonzero exit retained");
    }
    private sealed class Backend(UninstallEntry entry) : IUninstallBackend
    {
        public UninstallEntry Current = entry;
        public bool RemoveAfter, ThrowAfter, Executed;
        public int Executions;
        public UninstallMode LastMode;
        public UninstallExecution Execution = new(true, true, 0);
        public TaskCompletionSource<bool>? Wait;
        public TaskCompletionSource<bool> Entered = new();
        public UninstallSnapshot Scan(CancellationToken token) => new() { Entries = new[] { Current } };
        public UninstallEntry? Read(UninstallLocator locator, CancellationToken token)
        { if (Executed) { Executed = false; if (ThrowAfter) throw new IOException(); if (RemoveAfter) return null; } return Current; }
        public UninstallReview Review(UninstallEntry expected, UninstallMode mode, CancellationToken token) => new(expected with { ReviewedMode = mode }, "Unsigned example fixture");
        public async Task<UninstallExecution> ExecuteAsync(UninstallEntry expected, UninstallMode mode, CancellationToken token)
        { Executions++; LastMode = mode; Entered.TrySetResult(true); if (Wait is not null) await Wait.Task; Executed = true; return Execution; }
    }
}
