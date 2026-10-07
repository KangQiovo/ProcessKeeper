using System.Text.Json;
using System.Text.Json.Nodes;
using ProcessKeeper.App;

internal static class MicrosoftVisibilityPreferencesVerification
{
    internal static void Run(string root, string defaultsPath, Action<bool, string> check)
    {
        check(!new ViewPreferences().HideMicrosoftApps, "new view preferences leave Microsoft applications visible");
        using (var defaults = JsonDocument.Parse(File.ReadAllText(defaultsPath)))
            check(defaults.RootElement.GetProperty("View").TryGetProperty("HideMicrosoftApps", out var hide)
                && hide.ValueKind == JsonValueKind.False, "distributed default settings explicitly disable Microsoft hiding");
        var defaultBundle = SettingsBundleStore.ReadImport(defaultsPath);
        check(!defaultBundle.View.HideMicrosoftApps, "distributed default settings import leaves Microsoft applications visible");

        var directory = Path.Combine(root, "microsoft-visibility-preferences");
        var store = new ViewPreferencesStore(directory);
        check(!store.Load().HideMicrosoftApps && !Directory.Exists(directory), "missing view configuration uses visible Microsoft applications without writes");
        foreach (var hide in new[] { true, false })
        {
            var saved = new ViewPreferences(LiveRefresh: false, Language: "zh-Hant", InstalledDrive: "E:", GroupGamePlatforms: false, HideMicrosoftApps: hide);
            store.Save(saved);
            var text = File.ReadAllText(store.FilePath);
            check(store.Load() == saved, "explicit Microsoft visibility choice survives loading: " + hide);
            check(File.ReadAllText(store.FilePath) == text, "loading explicit Microsoft visibility does not rewrite the choice: " + hide);
            var export = Path.Combine(root, "microsoft-visibility-" + hide + ".json");
            var bundle = defaultBundle with { View = saved };
            SettingsBundleStore.Export(export, bundle);
            var imported = SettingsBundleStore.ReadImport(export);
            check(imported.View == saved, "all-settings backup preserves explicit Microsoft visibility and other view options: " + hide);
            var restoredDirectory = Path.Combine(root, "microsoft-restored-" + hide);
            SettingsBundleStore.CommitAll(restoredDirectory, imported);
            check(new ViewPreferencesStore(restoredDirectory).Load() == saved, "explicit all-settings restore preserves Microsoft visibility: " + hide);
        }

        var explicitChoice = new ViewPreferences(Language: "zh-Hant", InstalledDrive: "E:", GroupGamePlatforms: false, HideMicrosoftApps: true);
        foreach (var retainGrouping in new[] { true, false })
        {
            store.Save(explicitChoice);
            var oldView = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
            oldView.Remove("HideMicrosoftApps");
            if (!retainGrouping) oldView.Remove("GroupGamePlatforms");
            var text = oldView.ToJsonString();
            File.WriteAllText(store.FilePath, text);
            var expected = explicitChoice with { HideMicrosoftApps = false, GroupGamePlatforms = !retainGrouping };
            check(store.Load() == expected, "older view without Microsoft visibility uses false and preserves other options: " + retainGrouping);
            check(File.ReadAllText(store.FilePath) == text, "older view missing Microsoft visibility remains unchanged: " + retainGrouping);
            var oldBackup = JsonNode.Parse(File.ReadAllText(defaultsPath))!.AsObject();
            oldBackup["View"] = oldView.DeepClone();
            var export = Path.Combine(root, "microsoft-older-backup-" + retainGrouping + ".json");
            var backupText = oldBackup.ToJsonString();
            File.WriteAllText(export, backupText);
            var imported = SettingsBundleStore.ReadImport(export);
            check(imported.View == expected, "older all-settings backup defaults missing Microsoft visibility to false: " + retainGrouping);
            var target = Path.Combine(root, "microsoft-old-restored-" + retainGrouping);
            SettingsBundleStore.CommitAll(target, defaultBundle with { View = explicitChoice });
            SettingsBundleStore.CommitAll(target, imported);
            check(new ViewPreferencesStore(target).Load() == expected, "restoring older settings consistently leaves Microsoft visibility off: " + retainGrouping);
            check(File.ReadAllText(export) == backupText, "reading and restoring an older backup does not rewrite its source: " + retainGrouping);
            check(Directory.EnumerateDirectories(Path.Combine(target, "backups")).Any(backup =>
                new ViewPreferencesStore(backup).Load().HideMicrosoftApps), "older backup restore retains the previously explicit true choice in its backup: " + retainGrouping);
        }

        var resetDirectory = Path.Combine(root, "microsoft-default-restored");
        SettingsBundleStore.CommitAll(resetDirectory, defaultBundle with { View = explicitChoice });
        SettingsBundleStore.CommitAll(resetDirectory, defaultBundle);
        check(new ViewPreferencesStore(resetDirectory).Load() == defaultBundle.View && !defaultBundle.View.HideMicrosoftApps,
            "explicit default-settings restore resets Microsoft hiding to false");
        check(Directory.EnumerateDirectories(Path.Combine(resetDirectory, "backups")).Any(backup =>
            new ViewPreferencesStore(backup).Load().HideMicrosoftApps), "default-settings restore backs up the prior explicitly saved true choice");

        foreach (var invalid in new[] { "null", "0", "\"false\"", "[]", "{}" })
        {
            store.Save(explicitChoice);
            var document = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
            document["HideMicrosoftApps"] = JsonNode.Parse(invalid);
            var text = document.ToJsonString();
            File.WriteAllText(store.FilePath, text);
            var rejected = false;
            try { store.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(store.FilePath) == text, "invalid Microsoft visibility is rejected without replacing saved configuration: " + invalid);
            var backup = JsonNode.Parse(File.ReadAllText(defaultsPath))!.AsObject();
            backup["View"] = document;
            var path = Path.Combine(root, "microsoft-invalid-backup.json");
            var backupText = backup.ToJsonString();
            File.WriteAllText(path, backupText);
            rejected = false;
            try { SettingsBundleStore.ReadImport(path); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == backupText, "invalid Microsoft visibility in backup is rejected without modifying its source: " + invalid);
        }
    }
}
