using ProcessKeeper.App;
using ProcessKeeper.Core;
using System.Text.Json;

internal static class WhitelistScopeVerification
{
    internal static void Run(string root, SettingsBundle original, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "whitelist-scopes");
        var store = new WhitelistScopePreferencesStore(directory);
        check(store.Load() == new WhitelistScopePreferences(true, true, false, false), "whitelist defaults to running and installed pages only");
        var preferences = new WhitelistScopePreferences(false, true, true, true);
        store.Save(preferences); check(store.Load() == preferences, "custom whitelist page scopes save and reload");
        var file = Path.Combine(root, "scope-settings.json");
        var scoped = original with { WhitelistScope = preferences };
        SettingsBundleStore.Export(file, scoped);
        check(SettingsBundleStore.ReadImport(file).WhitelistScope == preferences, "all-settings export and import preserves all whitelist page scopes");
        var previous = File.ReadAllText(store.FilePath);
        SettingsBundleStore.CommitAll(directory, original);
        check(File.ReadAllText(store.FilePath) == previous, "old settings imports retain local whitelist page scopes");
        SettingsBundleStore.CommitAll(directory, scoped with { WhitelistScope = new() });
        check(store.Load() == new WhitelistScopePreferences(), "new settings import applies whitelist scopes atomically");
        foreach (var invalid in new[] { "null", "{}", "{\"Version\":1,\"Running\":\"true\",\"Installed\":true,\"Autoruns\":false,\"Uninstall\":false}",
            "{\"Version\":1,\"Running\":true,\"Running\":false,\"Installed\":true,\"Autoruns\":false,\"Uninstall\":false}",
            "{\"Version\":2,\"Running\":true,\"Installed\":true,\"Autoruns\":false,\"Uninstall\":false}" })
        {
            var rejected = false;
            try { using var json = JsonDocument.Parse(invalid); WhitelistScopePreferencesStore.Parse(json.RootElement); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "malformed, duplicate or unsupported whitelist scopes are rejected");
        }
        var bytes = File.ReadAllText(store.FilePath);
        File.WriteAllText(store.FilePath, "broken");
        var failed = false; try { store.Load(); } catch (JsonException) { failed = true; }
        check(failed && File.ReadAllText(store.FilePath) == "broken", "corrupt scope state fails visibly without overwriting the original");
        File.WriteAllText(store.FilePath, bytes);
        var rollback = Path.Combine(root, "scope-rollback");
        SettingsBundleStore.CommitAll(rollback, original with { WhitelistScope = preferences });
        var protectedBytes = File.ReadAllText(Path.Combine(rollback, "whitelist-scope.json"));
        try { SettingsBundleStore.CommitAll(rollback, original with { WhitelistScope = new() }, index => { if (index == 3) throw new IOException("scope fixture failure"); }); }
        catch (SettingsCommitException error) { check(error.RollbackSucceeded, "failed scope bundle commit reports successful rollback"); }
        check(File.ReadAllText(Path.Combine(rollback, "whitelist-scope.json")) == protectedBytes, "failed all-settings import leaves original whitelist scope intact");
        var targetPath = @"E:\Fixture\Editor\editor.exe";
        var application = new InstalledApplication { Name = "Editor", ApplicationKey = "fixture:editor",
            Executables = new[] { new InstalledExecutable { Path = targetPath } } };
        var applicationRules = new[] { new WhitelistRule { Kind = RuleKind.Application, Value = "fixture:editor" } };
        check(WhitelistActionMatcher.Matches(new[] { targetPath.ToUpperInvariant() }, applicationRules, ProcessSnapshot.Empty, new[] { application }),
            "startup and uninstall targets inherit exact installed application whitelist identity");
        check(!WhitelistActionMatcher.Matches(new[] { @"E:\Fixture\Other\editor.exe" }, applicationRules, ProcessSnapshot.Empty, new[] { application }),
            "an identical display name or executable filename does not inherit another application's whitelist");
        check(!WhitelistActionMatcher.Matches(new[] { targetPath }, new[] { applicationRules[0] with { Enabled = false } }, ProcessSnapshot.Empty, new[] { application }),
            "disabled whitelist rules do not protect startup or uninstall targets");
        var process = new ProcessRecord { Id = 123, Path = targetPath, Name = "editor.exe", ApplicationKey = "live:editor" };
        var snapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, new[] { process }, Array.Empty<ApplicationGroup>());
        check(WhitelistActionMatcher.Matches(new[] { targetPath }, new[] { applicationRules[0] with { Value = "live:editor" } }, snapshot, Array.Empty<InstalledApplication>()),
            "a verified live executable supplies its application identity to whitelist guards");
        var directoryRule = new WhitelistRule { Kind = RuleKind.Directory, Value = @"E:\Fixture\Editor" };
        check(WhitelistActionMatcher.Matches(new[] { targetPath }, new[] { directoryRule }, ProcessSnapshot.Empty, Array.Empty<InstalledApplication>()),
            "directory whitelist scope protects a concrete child executable");
        check(!WhitelistActionMatcher.Matches(new[] { @"E:\Fixture\Editor-copy\editor.exe", "", "\0" }, new[] { directoryRule }, ProcessSnapshot.Empty, Array.Empty<InstalledApplication>()),
            "directory-prefix siblings and invalid or empty targets do not match a whitelist rule");
    }
}
