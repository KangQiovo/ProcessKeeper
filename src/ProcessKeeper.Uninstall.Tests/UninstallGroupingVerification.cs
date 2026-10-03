using ProcessKeeper.Core;

internal static class UninstallGroupingVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var first = Entry("old", "Example app 1.0 (x86)", "Example team", "1.0");
        var second = Entry("new", "Example app 2.0 (x64)", "Example team", "2.0");
        var versions = UninstallDisplay.Group(new[] { first, second });
        check(versions.Count == 1 && versions[0].Entries.Count == 2, "registered version and architecture aliases have one application parent");
        check(ReferenceEquals(versions[0].Entries[0], first) && ReferenceEquals(versions[0].Entries[1], second), "grouping retains original immutable uninstall targets");
        check(UninstallDisplay.Group(new[] { first, first with { Id = "other-vendor", Publisher = "Unrelated team" } }).Count == 2,
            "same display name from different publishers remains separate");
        check(UninstallDisplay.Group(new[] { first, first with { Id = "other-product", Name = "Example app 2", Version = "" } }).Count == 2,
            "unregistered product numbers are not stripped as versions");
        var main = @"E:\Apps\Example\Example.exe";
        var local = Entry("local-name", "示例应用", "Example team", "1.0") with { ApplicationPaths = new[] { main } };
        var english = Entry("english-name", "Example app", "Example team", "1.0") with { ApplicationPaths = new[] { main.ToUpperInvariant() } };
        check(UninstallDisplay.Group(new[] { local, english }).Count == 1, "one exact associated main executable unifies localized registration names");
        check(UninstallDisplay.Group(new[] { local, english with { Publisher = "Unrelated team" } }).Count == 2,
            "shared path cannot erase conflicting publisher evidence");
        var unknown = english with { Id = "unknown-publisher", Publisher = "" };
        check(UninstallDisplay.Group(new[] { local, unknown }).Count == 1, "unambiguous associated main executable accepts one missing publisher");
        check(UninstallDisplay.Group(new[] { local, unknown, english with { Id = "other-publisher", Publisher = "Unrelated team" } }).Count == 3,
            "missing publisher never bridges conflicting publisher groups");
        check(UninstallDisplay.Group(new[] { local with { ApplicationPaths = Array.Empty<string>() }, english with { ApplicationPaths = Array.Empty<string>() } }).Count == 2,
            "shared installer and directory do not unify unrelated display names");
        check(UninstallDisplay.Group(new[] { local with { ApplicationPaths = new[] { @"C:\Windows\System32\msiexec.exe" } }, english with { ApplicationPaths = new[] { @"C:\Windows\System32\msiexec.exe" } } }).Count == 2,
            "shared executable hosts are excluded from application alias evidence");
        check(versions[0].Key == UninstallDisplay.Group(new[] { second, first })[0].Key, "parent identity is stable when registration order changes");
        check(UninstallDisplay.Group(new[] { local, english, local }).Single().Entries.Count == 2, "duplicate snapshot rows do not create extra children");
        var unknownFirst = Entry("unknown-first", "Unknown registration", "", "") with { ApplicationPaths = new[] { @"E:\Apps\First\Example.exe" } };
        var unknownSecond = unknownFirst with { Id = "unknown-second", Locator = new("HKCU", 64, "unknown-second"), ApplicationPaths = new[] { @"E:\Apps\Second\Example.exe" } };
        var publisherFirst = local with { Id = "publisher-first", ApplicationPaths = unknownFirst.ApplicationPaths };
        var publisherSecond = english with { Id = "publisher-second", Publisher = "Unrelated team", ApplicationPaths = unknownSecond.ApplicationPaths };
        check(UninstallDisplay.Group(new[] { unknownFirst, unknownSecond, publisherFirst, publisherSecond }).Count == 3,
            "same-name unknown publisher group cannot attach to one of two conflicting main executable owners");
    }

    private static UninstallEntry Entry(string id, string name, string publisher, string version) => new()
    {
        Id = id, Name = name, Publisher = publisher, Version = version,
        Locator = new("HKCU", 64, id), Fingerprint = "registration-" + id,
        ExecutableIdentity = "native-file", Command = new(@"E:\Apps\Example\uninstall.exe", "/remove", false),
        InstallLocation = @"E:\Apps\Example"
    };
}
