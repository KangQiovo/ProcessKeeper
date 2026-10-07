using ProcessKeeper.Core;

internal static class InstalledDuplicateVerification
{
    internal static void Run(Action<bool, string> check)
    {
        InstalledExecutable Exe(string path, string product = "", string company = "", string version = "") => new()
        {
            Path = path, Name = Path.GetFileName(path), Description = product,
            ProductName = product, CompanyName = company, FileVersion = version,
            ApplicationKey = "path:" + path.ToLowerInvariant()
        };
        InstalledApplication App(string id, string name, string publisher, string root, params InstalledExecutable[] files) => new()
        { Id = id, Name = name, Publisher = publisher, InstallLocation = root, Executables = files };
        const string product = "Broadcast Studio", vendor = "Broadcast Vendor";
        var component = Exe(@"E:\Apps\Editor\bin\editor.exe");
        var registry = App("arp", "Editor", "Editor Vendor", @"E:\Apps\Editor", component);
        var appPath = App("apppath", "editor.exe", "", @"E:\Apps\Editor\bin", component) with { EntryPaths = new[] { component.Path } };
        var exact = ApplicationPresentationGroups.MergeInstalled(new[] { registry, appPath });
        check(exact.Count == 1 && exact[0].Installations.Count == 2, "registered component and nested explicit entry sharing an exact EXE form one parent");
        var sharedTool = Exe(@"E:\VendorSuite\Tool\tool.exe");
        var suite = App("suite", "Suite A", "Vendor A", @"E:\VendorSuite", sharedTool);
        var tool = App("tool", "Tool B", "", @"E:\VendorSuite\Tool", sharedTool);
        check(ApplicationPresentationGroups.MergeInstalled(new[] { suite, tool }).Count == 2
            && ApplicationPresentationGroups.MergeInstalled(new[] { suite, tool with { Publisher = "Vendor A" } }).Count == 2,
            "a suite's directory scan does not claim a separately named nested application through a shared component alone");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { suite, tool with { EntryPaths = new[] { sharedTool.Path } } }).Count == 2,
            "an explicit child entry cannot turn a generic parent-suite component into the parent's main program");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { suite with { Executables = new[] { sharedTool with { Description = "Suite A" } } }, tool with { EntryPaths = new[] { sharedTool.Path } } }).Count == 2,
            "a heuristic product description cannot absorb an explicitly named child product");

        var versioned = Exe(@"E:\Apps\Broadcast\8.9.0.11148\broadcast.exe", product, vendor, "8.9.0.11148");
        var launcher = Exe(@"E:\Apps\Broadcast\broadcast.exe", product, vendor, "8.9.0.11148");
        var installed = App("broadcast:arp", product, vendor, @"E:\Apps\Broadcast\8.9.0.11148", versioned);
        var shortcut = App("broadcast:shortcut", product, "", @"E:\Apps\Broadcast", launcher) with { EntryPaths = new[] { launcher.Path } };
        var copied = ApplicationPresentationGroups.MergeInstalled(new[] { installed, shortcut });
        check(copied.Count == 1 && copied[0].Installations.Count == 2 && copied[0].Executables.Count == 2,
            "a product launcher and its direct version-directory copy group without losing either exact path");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { shortcut, installed })[0].Id == copied[0].Id
            && ApplicationPresentationGroups.MergeInstalled(copied)[0].Id == copied[0].Id, "duplicate family identity is order independent and idempotent");
        foreach (var altered in new[]
        {
            shortcut with { Executables = new[] { launcher with { CompanyName = "Other Vendor" } } },
            shortcut with { Executables = new[] { launcher with { ProductName = "Other Product" } } },
            shortcut with { Executables = new[] { launcher with { FileVersion = "" } } },
            shortcut with { Executables = new[] { launcher with { FileVersion = "9.0.0.0" } } },
            shortcut with { EntryPaths = Array.Empty<string>() },
            shortcut with { InstallLocation = @"E:\Apps\OtherBroadcast", Executables = new[] { launcher with { Path = @"E:\Apps\OtherBroadcast\broadcast.exe" } }, EntryPaths = new[] { @"E:\Apps\OtherBroadcast\broadcast.exe" } }
        }) check(ApplicationPresentationGroups.MergeInstalled(new[] { installed, altered }).Count == 2,
            "copied-launcher grouping requires matching product, vendor, version, explicit entry and related product directory");
        var nonVersion = installed with { InstallLocation = @"E:\Apps\Broadcast\plugins", Executables = new[] { versioned with { Path = @"E:\Apps\Broadcast\plugins\broadcast.exe" } } };
        check(ApplicationPresentationGroups.MergeInstalled(new[] { nonVersion, shortcut }).Count == 2, "a generic nested helper folder is not a product version directory");
        var utility = installed with { Executables = new[] { versioned with { Name = "uninstall.exe", Path = @"E:\Apps\Broadcast\8.9.0.11148\uninstall.exe" } } };
        var utilityShortcut = shortcut with { Executables = new[] { launcher with { Name = "uninstall.exe", Path = @"E:\Apps\Broadcast\uninstall.exe" } }, EntryPaths = new[] { @"E:\Apps\Broadcast\uninstall.exe" } };
        check(ApplicationPresentationGroups.MergeInstalled(new[] { utility, utilityShortcut }).Count == 2, "uninstaller metadata cannot establish a copied main-program relationship");

        var desktop = App("sync:arp", "Sync Client", "sync-client", @"E:\Apps\Sync", Exe(@"E:\Apps\Sync\sync-client.exe"));
        var package = App("sync:package", "Sync Client", "Registered Sync Company", @"F:\PackageStore\Sync_1.0") with
        { ApplicationKey = "package:sync.client_123", ExternalInstallLocation = @"E:\Apps\Sync" };
        var sparse = ApplicationPresentationGroups.MergeInstalled(new[] { desktop, package });
        check(sparse.Count == 1 && sparse[0].Installations.Count == 2 && sparse[0].Executables.Count == 1
            && sparse[0].Installations.Any(app => app.ApplicationKey == package.ApplicationKey), "a sparse package with an exact registered external product directory groups with its desktop installation");
        check(InstalledApplicationSearch.Drives(new[] { package }).Contains("E:") && InstalledApplicationSearch.OnDrive(package, "E:")
            && InstalledApplicationSearch.MatchesApplication(package, @"E:\Apps\Sync"), "an unreadable sparse package retains its registered external location in drive filters and path searches");
        foreach (var unrelated in new[]
        {
            package with { ExternalInstallLocation = "" },
            package with { ExternalInstallLocation = @"E:\Apps\OtherSync" },
            package with { Name = "Other Sync Client" },
            package with { ExternalInstallLocation = @"E:\Apps" },
            package with { ApplicationKey = "" }
        }) check(ApplicationPresentationGroups.MergeInstalled(new[] { desktop, unrelated }).Count == 2,
            "same names alone, generic directories and unregistered external hints cannot merge an empty package");
        var conflicting = desktop with { Id = "sync:other", Publisher = "Other Vendor" };
        check(ApplicationPresentationGroups.MergeInstalled(new[] { desktop, conflicting, package }).Count == 3,
            "external package association is refused when the exact product directory has conflicting registered vendors");
        var packageVersion = package with { Id = "sync:package2", InstallLocation = @"F:\PackageStore\Sync_2.0", ExternalInstallLocation = @"F:\Apps\Sync" };
        var withVersion = ApplicationPresentationGroups.MergeInstalled(new[] { desktop, package, packageVersion });
        check(withVersion.Count == 1 && withVersion[0].Installations.Count == 3 && withVersion[0].Id == sparse[0].Id,
            "a proven external desktop association preserves every version of the same registered package family");
        var otherDesktop = desktop with { Id = "sync:other-drive", Publisher = "Other Vendor", InstallLocation = @"F:\Apps\Sync", Executables = new[] { Exe(@"F:\Apps\Sync\sync-client.exe") } };
        var conflictingExternal = ApplicationPresentationGroups.MergeInstalled(new[] { desktop, otherDesktop, package, packageVersion });
        check(conflictingExternal.Count == 3 && conflictingExternal.Single(app => app.ApplicationKey == package.ApplicationKey).Installations.Count == 2,
            "conflicting external vendors leave desktop installations separate while preserving the original package family");
        var startup = new[] { new AutorunEntry { Id = "sync:start", TargetPath = desktop.Executables[0].Path }, new AutorunEntry { Id = "sync:helper", TargetPath = @"E:\Apps\Sync\provider.exe" } };
        check(ApplicationPresentationGroups.GroupAutoruns(startup, sparse, ProcessSnapshot.Empty).Single().Entries.Select(entry => entry.Id).SequenceEqual(startup.Select(entry => entry.Id)),
            "merged external installation preserves exact startup actions under one parent");
        var rules = new[] { new WhitelistRule { Id = "sync:exe", Kind = RuleKind.ExecutablePath, Value = desktop.Executables[0].Path }, new WhitelistRule { Id = "sync:pkg", Kind = RuleKind.Application, Value = package.ApplicationKey, Enabled = false } };
        var groupedRules = ApplicationPresentationGroups.GroupRules(rules, sparse, ProcessSnapshot.Empty);
        check(groupedRules.Count == 1 && groupedRules[0].Rules.SequenceEqual(rules),
            "desktop and package whitelist identities remain distinct and preserve enabled states");
        check(ApplicationPresentationGroups.MergeInstalled(new[] { desktop, package with { Publisher = desktop.Publisher, ExternalInstallLocation = "" } }).Count == 2,
            "matching display publisher alone cannot associate an empty package with a desktop app");
        var samePublisherPackage = package with { Publisher = desktop.Publisher };
        var samePublisherVersion = packageVersion with { Publisher = desktop.Publisher };
        check(ApplicationPresentationGroups.MergeInstalled(new[] { desktop, otherDesktop, samePublisherPackage, samePublisherVersion }).Count == 3,
            "a conflicting external package family cannot bypass its refused association via a matching weak publisher label");

        var bridgeA = App("bridge:a", "A", "Vendor A", @"E:\Bridge", Exe(@"E:\Bridge\a.exe")) with { EntryPaths = new[] { @"E:\Bridge\a.exe" } };
        var bridgeB = App("bridge:b", "B", "", @"E:\Bridge", Exe(@"E:\Bridge\a.exe"), Exe(@"E:\Bridge\b.exe")) with { EntryPaths = new[] { @"E:\Bridge\a.exe", @"E:\Bridge\b.exe" } };
        var bridgeC = App("bridge:c", "C", "", @"E:\Bridge", Exe(@"E:\Bridge\b.exe"), Exe(@"E:\Bridge\c.exe")) with { EntryPaths = new[] { @"E:\Bridge\b.exe", @"E:\Bridge\c.exe" } };
        var bridgeD = App("bridge:d", "D", "Vendor B", @"E:\Bridge", Exe(@"E:\Bridge\c.exe")) with { EntryPaths = new[] { @"E:\Bridge\c.exe" } };
        var chained = ApplicationPresentationGroups.MergeInstalled(new[] { bridgeA, bridgeB, bridgeC, bridgeD });
        var reversed = ApplicationPresentationGroups.MergeInstalled(new[] { bridgeD, bridgeC, bridgeB, bridgeA });
        string Membership(InstalledApplication app) => string.Join(",", (app.Installations.Count == 0 ? new[] { app } : app.Installations).Select(member => member.Id).Order(StringComparer.Ordinal));
        check(chained.Count == 3 && chained.Any(app => Membership(app) == "bridge:b,bridge:c")
            && chained.Select(Membership).Order().SequenceEqual(reversed.Select(Membership).Order()),
            "a chain of missing-publisher records cannot attach to different vendors according to scan order");
        var componentPackage = package with { Id = "sync:component", Publisher = "Shell Component Vendor", ApplicationKey = "package:sync.component_456" };
        var components = ApplicationPresentationGroups.MergeInstalled(new[] { desktop, package, componentPackage });
        check(components.Count == 1 && components[0].Installations.Count == 3
            && components[0].Installations.Select(app => app.ApplicationKey).Where(value => value.StartsWith("package:")).Distinct().Count() == 2,
            "multiple registered component packages may share an exact external desktop owner while retaining each package identity");
        var copiedInventory = Enumerable.Range(0, 1000).SelectMany(index =>
        {
            var name = "Studio " + index; var root = @"E:\Stress\Product" + index;
            var child = Exe(root + @"\1.2.3\studio.exe", name, vendor, "1.2.3");
            var entry = Exe(root + @"\studio.exe", name, vendor, "1.2.3");
            return new[] { App("stress:v:" + index, name, vendor, root + @"\1.2.3", child),
                App("stress:s:" + index, name, "", root, entry) with { EntryPaths = new[] { entry.Path } } };
        }).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var copiedStress = ApplicationPresentationGroups.MergeInstalled(copiedInventory); clock.Stop();
        check(copiedStress.Count == 1000 && copiedStress.All(app => app.Installations.Count == 2)
            && clock.Elapsed < TimeSpan.FromSeconds(3), "2,000 copied-launcher records group with a bounded metadata-only work budget");
        var sparseInventory = Enumerable.Range(0, 1500).Select(index => package with { Id = "stress:package:" + index }).Append(desktop).ToArray();
        clock.Restart(); var sparseStress = ApplicationPresentationGroups.MergeInstalled(sparseInventory); clock.Stop();
        check(sparseStress.Count == 1 && sparseStress[0].Installations.Count == sparseInventory.Length
            && clock.Elapsed < TimeSpan.FromSeconds(3), "1,500 sparse package versions share one external-owner plan within the work budget");
    }
}
