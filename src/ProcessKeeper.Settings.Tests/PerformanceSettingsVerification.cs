using ProcessKeeper.App;
using ProcessKeeper.Core;
using System.Text.Json.Nodes;

internal static class PerformanceSettingsVerification
{
    internal static void Run(string root, SettingsBundle original, Action<bool, string> check)
    {
        var preferences = new PerformancePreferences(Enabled: true, Display: PerformanceDisplay.Overlay,
            Detailed: true, UseAcrylic: true, Horizontal: true, Locked: true, X: -1600, Y: 100, Width: 400, Height: 130, CompactLine: true);
        var bundle = original with { Updates = new UpdatePreferences(), Performance = preferences };
        var path = Path.Combine(root, "performance-settings.json");
        SettingsBundleStore.Export(path, bundle);
        check(SettingsBundleStore.ReadImport(path).Performance == preferences, "all settings includes single-line layout, display mode, appearance, position, size and lock");
        var document = File.ReadAllText(path);
        foreach (var invalid in new[] { "null", "{}", "[]", "\"Overlay\"" })
        {
            var json = JsonNode.Parse(document)!.AsObject(); json["Performance"] = JsonNode.Parse(invalid);
            File.WriteAllText(path, json.ToJsonString());
            var rejected = false; try { SettingsBundleStore.ReadImport(path); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid performance section rejects the complete settings bundle");
        }
        var directory = Path.Combine(root, "performance-preservation");
        SettingsBundleStore.CommitAll(directory, bundle);
        var before = File.ReadAllText(Path.Combine(directory, "performance.json"));
        SettingsBundleStore.CommitAll(directory, original);
        check(File.ReadAllText(Path.Combine(directory, "performance.json")) == before, "older settings imports retain the performance preference file");
        check(new PerformancePreferencesStore(directory).Load() == preferences, "restored performance settings are readable by the running display");
        var names = new[] { "whitelist.json", "appearance.json", "view.json", "update.json", "performance.json" };
        for (var failure = 0; failure < names.Length; failure++)
        foreach (var existing in new[] { false, true })
        {
            var destination = Path.Combine(root, "performance-rollback-" + failure + existing);
            if (existing) SettingsBundleStore.CommitAll(destination, original with { Updates = new UpdatePreferences(), Performance = new PerformancePreferences() });
            var oldFiles = names.ToDictionary(name => name, name => File.Exists(Path.Combine(destination, name)) ? File.ReadAllText(Path.Combine(destination, name)) : null);
            var rejected = false;
            try { SettingsBundleStore.CommitAll(destination, bundle, index => { if (index == failure) throw new IOException("Injected performance backup failure"); }); }
            catch (SettingsCommitException ex) { rejected = ex.RollbackSucceeded; }
            check(rejected && names.All(name => oldFiles[name] is null ? !File.Exists(Path.Combine(destination, name)) : File.ReadAllText(Path.Combine(destination, name)) == oldFiles[name]),
                "five-file restore rolls back content or absence at " + failure + " existing=" + existing);
        }
    }
}
