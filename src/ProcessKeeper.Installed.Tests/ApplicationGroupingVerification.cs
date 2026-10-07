using ProcessKeeper.Core;

internal static class ApplicationGroupingVerification
{
    internal static void Run(Action<bool, string> check)
    {
        InstalledDuplicateVerification.Run(check);
        InstalledApplication App(string id, string name, string publisher, string root, params string[] files) => new()
        {
            Id = id, Name = name, Publisher = publisher, InstallLocation = root,
            EntryPaths = files.Take(1).ToArray(), Executables = files.Select(path => new InstalledExecutable
            { Path = path, Name = Path.GetFileName(path), ApplicationKey = "path:" + path.ToLowerInvariant() }).ToArray()
        };
        var a = App("registry", "Fixture Editor 1.2.0 (x64)", "Fixture Inc.", @"E:\Apps\Editor", @"E:\Apps\Editor\editor.exe");
        var alias = App("apppath", "editor.exe", "", @"E:\Apps\Editor", @"e:\apps\editor\EDITOR.exe", @"E:\Apps\Editor\helper.exe");
        var version = App("version", "Fixture Editor 2.0.0", "Fixture Inc.", @"F:\Apps\Editor", @"F:\Apps\Editor\editor.exe");
        var other = App("other", "Fixture Editor 1.2.0", "Other Publisher", @"G:\Apps\Editor", @"G:\Apps\Editor\editor.exe");
        var merged = ApplicationPresentationGroups.MergeInstalled(new[] { a, alias, version, other });
        check(merged.Count == 2, "same application registrations and versions form one parent, different publishers stay separate");
        var family = merged.Single(app => app.Publisher == "Fixture Inc.");
        check(family.Installations.Count == 3 && family.Executables.Count == 3, "group keeps original installation records and distinct exact executables");
        check(family.Executables.Any(exe => exe.Path.StartsWith("F:", StringComparison.Ordinal)), "group preserves another drive/version");
        check(InstalledApplicationSearch.MatchesApplication(family, "2.0.0") && InstalledApplicationSearch.OnDrive(family, "F:"), "member metadata remains searchable and drive-filterable");
        var unreadableVersion = App("unreadable", "Fixture Editor 3.0.0", "Fixture Inc.", @"H:\Apps\Editor");
        var unreadableFamily = ApplicationPresentationGroups.MergeInstalled(new[] { a, unreadableVersion }).Single();
        check(InstalledApplicationSearch.Drives(new[] { unreadableFamily }).Contains("H:") && InstalledApplicationSearch.OnDrive(unreadableFamily, "H:"), "a secondary installation root remains drive-filterable without readable executable components");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { version, alias, a, other }).Single(app => app.Publisher == "Fixture Inc.").Id == family.Id, "group key is independent of inventory order");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { a, alias, version }).Single().Id == family.Id, "unrelated app removal does not change family key");
        var roles = new InstalledExecutableIdentity();
        check(family.Executables.Where(exe => Path.GetFileName(exe.Path).Equals("editor.exe", StringComparison.OrdinalIgnoreCase)).All(exe => roles.Classify(family, exe).Role == InstalledExecutableRole.Main), "each original version keeps its own main-executable evidence");
        check(roles.ResolveMain(family).Status == InstalledMainIdentityStatus.Ambiguous, "multiple real main paths do not silently select the first EXE");
        var sharedHost = new[]
        {
            App("host1", "Alpha", "Alpha Inc", @"E:\Apps\Alpha", @"C:\Windows\System32\rundll32.exe"),
            App("host2", "Beta", "Beta Inc", @"E:\Apps\Beta", @"C:\Windows\System32\rundll32.exe")
        };
        check(ApplicationPresentationGroups.MergeInstalled(sharedHost).Count == 2, "a shared command host never establishes app ownership");
        var entries = new[]
        {
            new AutorunEntry { Id = "run", Name = "Editor startup", TargetPath = a.Executables[0].Path, SourceKind = AutorunSourceKind.RegistryRun },
            new AutorunEntry { Id = "task", Name = "Editor updater", TargetPath = alias.Executables[1].Path, SourceKind = AutorunSourceKind.ScheduledTask },
            new AutorunEntry { Id = "hosta", Name = "Shared host A", TargetPath = @"C:\Windows\System32\rundll32.exe" },
            new AutorunEntry { Id = "hostb", Name = "Shared host B", TargetPath = @"C:\Windows\System32\rundll32.exe" }
        };
        var autoruns = ApplicationPresentationGroups.GroupAutoruns(entries, merged, ProcessSnapshot.Empty);
        check(autoruns.Count == 3 && autoruns.Any(group => group.Entries.Count == 2), "startup sources belonging to the same installed application share one expandable parent");
        check(autoruns.SelectMany(group => group.Entries).Select(entry => entry.Id).Order().SequenceEqual(entries.Select(entry => entry.Id).Order()), "all exact startup mutation targets survive grouping");
        var rules = new[]
        {
            new WhitelistRule { Id = "main", Name = "Editor | main", Kind = RuleKind.ExecutablePath, Value = a.Executables[0].Path },
            new WhitelistRule { Id = "helper", Name = "Editor | helper", Kind = RuleKind.ExecutablePath, Value = alias.Executables[1].Path, Enabled = false }
        };
        var ruleGroups = ApplicationPresentationGroups.GroupRules(rules, merged, ProcessSnapshot.Empty);
        check(ruleGroups.Count == 1 && ruleGroups[0].Rules.Count == 2 && !ruleGroups[0].Rules[1].Enabled, "whitelist group preserves individual rule IDs and disabled states");
        check(ApplicationPresentationGroups.GroupRules(new[] { new WhitelistRule { Id = "A", Kind = RuleKind.ExecutablePath, Value = @"E:\Missing\A.exe" },
            new WhitelistRule { Id = "a", Kind = RuleKind.ExecutablePath, Value = @"F:\Missing\B.exe" } }, Array.Empty<InstalledApplication>(), ProcessSnapshot.Empty).Count == 2,
            "unresolved whitelist rule IDs retain their case-sensitive identity");
        var blankBridge = App("bridge", "Alias", "", @"E:\Apps\Editor", a.Executables[0].Path, other.Executables[0].Path)
            with { EntryPaths = new[] { a.Executables[0].Path, other.Executables[0].Path } };
        check(ApplicationPresentationGroups.MergeInstalled(new[] { a, blankBridge, other }).Count == 3, "blank publisher cannot bridge conflicting publishers via different evidence buckets");
        check(ApplicationPresentationGroups.NormalizeName("ＥＤＩＴＯＲ 1.2.0 (x64)") == "editor", "Unicode width and explicit version suffixes normalize consistently");
        check(ApplicationPresentationGroups.NormalizeName("Editor\ud800").StartsWith("editor", StringComparison.Ordinal), "malformed registry metadata cannot crash application grouping");
        var runtime = new[]
        {
            new ApplicationGroup { Key = "path:a", Name = "Fixture Editor", Company = "Fixture Inc.", Processes = new[] { new ProcessRecord { Id = 1, StartTimeUtcTicks = 1, Path = a.Executables[0].Path, ApplicationKey = "path:a" } } },
            new ApplicationGroup { Key = "path:b", Name = "Fixture Editor", Company = "Fixture Inc.", Processes = new[] { new ProcessRecord { Id = 2, StartTimeUtcTicks = 2, Path = version.Executables[0].Path, ApplicationKey = "path:b" } } }
        };
        var running = ApplicationPresentationGroups.MergeRunning(runtime);
        check(running.Count == 1 && running[0].Processes.Count == 2, "runtime product/publisher evidence merges separate executable parents");
        check(running[0].Processes.Select(process => process.ApplicationKey).SequenceEqual(new[] { "path:a", "path:b" }), "runtime grouping does not rewrite protection identities");
        check(ApplicationPresentationGroups.GetRunningRules(running[0]).Select(rule => rule.Value).Order().SequenceEqual(new[] { "path:a", "path:b" }), "keeping a merged runtime app retains every exact original identity");
        check(ApplicationPresentationGroups.MergeRunning(runtime.Select(app => app with { Name = " | " }).ToArray()).Count == 2, "empty product metadata cannot establish runtime ownership");
        var aliases = Enumerable.Range(0, 2000).Select(index => App("scale:" + index, "Editor alias " + index, "Fixture Inc.", a.InstallLocation, a.Executables[0].Path)).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew(); var large = ApplicationPresentationGroups.MergeInstalled(aliases); clock.Stop();
        check(large.Count == 1 && large[0].Installations.Count == aliases.Length && clock.Elapsed < TimeSpan.FromSeconds(3), "large duplicate-registration inventory groups within a bounded linear-work budget");
    }
}
