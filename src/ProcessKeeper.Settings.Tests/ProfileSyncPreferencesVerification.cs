using System.Text.Json.Nodes;
using ProcessKeeper.App;

internal static class ProfileSyncPreferencesVerification
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "profile-sync-preferences");
        var store = new ViewPreferencesStore(directory);
        check(store.Load().SyncEmptyProfileRules && !Directory.Exists(directory), "empty-profile sync defaults on without creating settings");
        var disabled = new ViewPreferences(SyncEmptyProfileRules: false);
        store.Save(disabled);
        check(!new ViewPreferencesStore(directory).Load().SyncEmptyProfileRules, "disabled profile sync persists across reloads");
        var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        document.Remove("SyncEmptyProfileRules");
        File.WriteAllText(store.FilePath, document.ToJsonString());
        check(store.Load().SyncEmptyProfileRules && !File.ReadAllText(store.FilePath).Contains("SyncEmptyProfileRules"), "older settings default profile sync on without rewriting the file");
        foreach (var invalid in new[] { "null", "0", "\"true\"", "[]" })
        {
            document["SyncEmptyProfileRules"] = JsonNode.Parse(invalid);
            var text = document.ToJsonString(); File.WriteAllText(store.FilePath, text);
            var rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(store.FilePath) == text, "invalid profile-sync setting is rejected without data loss");
        }
        var export = Path.Combine(root, "profile-sync-export.json");
        SettingsBundleStore.Export(export, new SettingsBundle(Array.Empty<ProcessKeeper.Core.WhitelistRule>(), new(), disabled));
        var imported = SettingsBundleStore.ReadImport(export);
        check(!imported.View.SyncEmptyProfileRules, "all-settings backup includes profile-sync preference");
        var destination = Path.Combine(root, "profile-sync-restored");
        SettingsBundleStore.CommitAll(destination, imported);
        check(!new ViewPreferencesStore(destination).Load().SyncEmptyProfileRules, "restoring all settings restores disabled profile sync");
    }
}
