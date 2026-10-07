using ProcessKeeper.Core;

internal static class InstalledRegistrationPresentationTests
{
    internal static void Run(Action<bool, string> check)
    {
        InstalledExecutable Exe(string path, string product = "", string company = "", string version = "") => new()
        { Path = path, Name = Path.GetFileName(path), Description = product, ProductName = product, CompanyName = company,
            FileVersion = version, ApplicationKey = "path:" + path.ToLowerInvariant() };
        InstalledApplication App(string id, string name, string publisher, string root, params InstalledExecutable[] files) => new()
        { Id = id, Name = name, Publisher = publisher, InstallLocation = root, Executables = files };
        IReadOnlyList<InstalledApplication> Merge(InstalledApplication[] apps, InstalledPresentationRegistration[] registrations,
            InstalledAdvertisedShortcut[]? shortcuts = null, InstalledRuntimeRegistration[]? runtimes = null) =>
            ApplicationPresentationGroups.MergeInstalled(InstalledRegistrationPresentation.Associate(apps, registrations, shortcuts ?? [], runtimes ?? []));
        const string guid = "{C9A84C4C-5E47-409C-B705-B5D31A2E4C50}";
        var msiMain = Exe(@"E:\Programs\Update Client\SoftwareUpdate.exe", "Update Client", "Update Vendor", "2.6.3.1");
        var msiIcon = Exe(@"C:\Windows\Installer\" + guid + @"\UpdateIcon.exe");
        var msi = App("msi:arp", "Update Client", "Update Vendor", @"E:\Programs\Update Client", msiMain);
        var advertised = App("msi:shortcut", "Update Client", "", Path.GetDirectoryName(msiIcon.Path)!, msiIcon) with { EntryPaths = [msiIcon.Path] };
        var msiRegistration = new InstalledPresentationRegistration(msi.Id, "HKLM:32:" + guid, msi.Name, msi.Publisher, "2.6.3.1", msi.InstallLocation, "", guid);
        var shortcut = new InstalledAdvertisedShortcut(advertised.Id, guid, msiIcon.Path, msiMain.Path);
        var msiMerged = Merge([msi, advertised], [msiRegistration], [shortcut]);
        check(msiMerged.Count == 1 && msiMerged[0].Installations.Count == 2 && msiMerged[0].Executables.Count == 2,
            "MSI advertised shortcut uses its exact registered ProductCode without dropping its icon executable or main installation");
        var msiRoles = new InstalledExecutableIdentity();
        check(msiRoles.ResolveMain(msiMerged[0]).ExecutablePath == msiMain.Path
            && msiRoles.Classify(msiMerged[0], msiIcon).Role is not (InstalledExecutableRole.Main or InstalledExecutableRole.PotentialMain),
            "read-only MSI component registration identifies the real main executable and retains the advertised icon as a component");
        check(Merge([msi, advertised], [msiRegistration], [shortcut with { MainExecutablePath = @"E:\OtherProduct\main.exe" }]).Count == 2,
            "an MSI descriptor cannot associate a main executable outside the registered original product record");
        check(Merge([msi, advertised], [], [shortcut]).Count == 2, "an advertised ProductCode without an existing inventory registration cannot group");
        check(Merge([msi, advertised], [msiRegistration]).Count == 2, "a Windows Installer GUID-shaped path alone is not advertised shortcut evidence");
        check(Merge([msi, advertised], [msiRegistration], [shortcut with { ProductCode = "{13B8478F-E72F-484D-B2E5-078B7B97C50D}" }]).Count == 2,
            "an advertised ProductCode cannot claim another installed product");
        check(Merge([msi, advertised], [msiRegistration], [shortcut with { TargetPath = @"E:\Forged\icon.exe" }]).Count == 2,
            "advertised registration evidence must refer to the exact shortcut target in its original inventory record");
        check(Merge([msi, advertised with { Publisher = "Unrelated Vendor" }], [msiRegistration], [shortcut]).Count == 2,
            "an advertised shortcut cannot override a conflicting original publisher");

        var remover = Exe(@"E:\Vendor\Access\RemoveAccess.exe", "Access Client", "Access Vendor", "1.0.0.0");
        var accessMain = Exe(@"E:\Vendor\Access\AccessCore.exe", "Access Client", "Access Vendor, Inc.", "2.21.0.18");
        var brokenRoot = App("access:arp", "Access Client", "Access Vendor, Inc.", @"F:\OldInstall", remover);
        var accessShortcut = App("access:shortcut", "Access Client", "", @"E:\Vendor\Access", accessMain) with { EntryPaths = [accessMain.Path] };
        var accessRegistration = new InstalledPresentationRegistration(brokenRoot.Id, "HKLM:64:access-product", brokenRoot.Name, brokenRoot.Publisher,
            "2.21.0.559", brokenRoot.InstallLocation, remover.Path);
        var accessMerged = Merge([brokenRoot, accessShortcut], [accessRegistration]);
        check(accessMerged.Count == 1 && accessMerged[0].Installations.Count == 2 && accessMerged[0].Executables.Count == 2,
            "an exact registered icon directory links a product's explicit main entry despite stale InstallLocation while retaining both records");
        foreach (var wrong in new[] {
            accessShortcut with { Executables = [accessMain with { CompanyName = "Other Vendor" }] },
            accessShortcut with { Executables = [accessMain with { ProductName = "Other Product" }] },
            accessShortcut with { EntryPaths = [] },
            accessShortcut with { Executables = [accessMain with { FileVersion = "" }] },
            accessShortcut with { InstallLocation = @"E:\Other\Access", Executables = [accessMain with { Path = @"E:\Other\Access\AccessCore.exe" }], EntryPaths = [@"E:\Other\Access\AccessCore.exe"] },
            accessShortcut with { Executables = [accessMain with { Path = @"E:\Vendor\Access\setup.exe", Name = "setup.exe" }], EntryPaths = [@"E:\Vendor\Access\setup.exe"] }
        }) check(Merge([brokenRoot, wrong], [accessRegistration]).Count == 2,
            "registered icon association requires exact product directory, publisher metadata, executable version and an explicit main role");

        var installer = Exe(@"E:\Cache\python-3.14.3-amd64.exe", "Python 3.14.3 (64-bit)", "Python Software Foundation", "3.14.3150.0");
        var python = Exe(@"E:\Python314\python.exe", "Python", "Python Software Foundation", "3.14.3");
        var burn = App("python:burn", "Python 3.14.3 (64-bit)", "Python Software Foundation", @"E:\Cache", installer);
        var pythonShortcut = App("python:shortcut", "Python 3.14 (64-bit)", "", @"E:\Python314", python) with { EntryPaths = [python.Path] };
        var burnRegistration = new InstalledPresentationRegistration(burn.Id, "HKCU:64:python-bundle", burn.Name, burn.Publisher,
            "3.14.3150.0", "", installer.Path, BundleProviderKey: "CPython-3.14", BundleCachePath: installer.Path);
        var runtime = new InstalledRuntimeRegistration("CPython-3.14", "3.14", "64bit", pythonShortcut.InstallLocation, python.Path);
        var pythonMerged = Merge([burn, pythonShortcut], [burnRegistration], runtimes: [runtime]);
        check(pythonMerged.Count == 1 && pythonMerged[0].Installations.Count == 2 && pythonMerged[0].Executables.Count == 2,
            "Burn cache and PEP514 exact provider/version/architecture registration form one Python parent without dropping installer evidence");
        var pythonRoles = new InstalledExecutableIdentity();
        check(pythonRoles.ResolveMain(pythonMerged[0]).ExecutablePath == python.Path
            && pythonRoles.Classify(pythonMerged[0], installer).Role is not (InstalledExecutableRole.Main or InstalledExecutableRole.PotentialMain),
            "registered Python runtime is the main executable while the Burn cache is retained as an installer component");
        check(pythonRoles.OrderExecutables(pythonMerged[0])[0].Path == python.Path
            && pythonRoles.Classify(pythonMerged[0], installer).Role == InstalledExecutableRole.Helper,
            "registered Python runtime is the first displayed/default component and the cached installer uses the existing helper role");
        bool UnregisteredRolesRetained(IReadOnlyList<InstalledApplication> apps) => apps.Count == 2
            && new InstalledExecutableIdentity().Classify(apps.Single(app => app.Id == pythonShortcut.Id), python).Role == InstalledExecutableRole.Helper
            && new InstalledExecutableIdentity().Classify(apps.Single(app => app.Id == burn.Id), installer).Role == InstalledExecutableRole.PotentialMain;
        check(UnregisteredRolesRetained(Merge([burn, pythonShortcut], [], runtimes: [runtime])),
            "Python without exact registration evidence retains generic host protection and does not assign cache installer roles");
        foreach (var wrongRuntime in new[] {
            runtime with { ProviderKey = "CPython-3.13" }, runtime with { Version = "3.13" }, runtime with { Architecture = "32bit" },
            runtime with { ExecutablePath = @"E:\OtherPython\python.exe" }, runtime with { InstallLocation = @"E:\OtherPython" }
        }) check(UnregisteredRolesRetained(Merge([burn, pythonShortcut], [burnRegistration], runtimes: [wrongRuntime])),
            "Burn to runtime association rejects another provider, release, architecture or registered executable root");
        foreach (var wrongPython in new[] {
            pythonShortcut with { Executables = [python with { CompanyName = "Other Foundation" }] },
            pythonShortcut with { Executables = [python with { FileVersion = "3.14.2" }] },
            pythonShortcut with { Executables = [python with { ProductName = "Other Runtime" }] }
        }) check(UnregisteredRolesRetained(Merge([burn, wrongPython], [burnRegistration], runtimes: [runtime])),
            "Burn to runtime association requires matching executable product, publisher and exact release metadata");
        check(UnregisteredRolesRetained(Merge([burn, pythonShortcut], [burnRegistration with { BundleCachePath = @"E:\OtherCache\python.exe" }], runtimes: [runtime])),
            "a registered Burn cache path must be one of that original installer record's exact executables");
        check(UnregisteredRolesRetained(Merge([burn, pythonShortcut with { Publisher = "Unrelated Foundation" }], [burnRegistration], runtimes: [runtime])),
            "a conflicting original runtime publisher cannot mint main or installer roles from Burn registration evidence");
        check(UnregisteredRolesRetained(Merge([burn, pythonShortcut], [burnRegistration with { Version = "3.14.3151.0" }], runtimes: [runtime])),
            "an installer version inconsistent with the original registration cannot mint executable roles");
        check(UnregisteredRolesRetained(Merge([burn, pythonShortcut], [burnRegistration with { BundleProviderKey = "OtherPython-3.14" }], runtimes: [runtime])),
            "a provider name lookalike cannot mint Python main or cached installer roles");
        var byOriginal = InstalledRegistrationPresentation.Associate([msi, advertised], [msiRegistration], [shortcut], []);
        check(ApplicationPresentationGroups.MergeInstalled(ApplicationPresentationGroups.MergeInstalled(byOriginal))[0].Id == msiMerged[0].Id,
            "registration-linked presentation identity remains idempotent and preserves original mutation identities");
        var actions = new[] { new AutorunEntry { Id = "main", TargetPath = msiMain.Path }, new AutorunEntry { Id = "icon", TargetPath = msiIcon.Path } };
        var actionGroups = ApplicationPresentationGroups.GroupAutoruns(actions, msiMerged, ProcessSnapshot.Empty);
        check(actionGroups.Count == 1 && actionGroups[0].Entries.Count == 2,
            "registration-linked families share autorun presentation while retaining each exact underlying action");
    }
}
