using System.Text.Json;
using System.Text.Json.Nodes;
using ProcessKeeper.App;

internal static class AppearanceParametersVerification
{
    internal static void Run(string root, Action<bool, string> check)
    {
        const string oldJson = "{\"Version\":1,\"BackdropEnabled\":false,\"Material\":\"Acrylic\",\"Theme\":\"Dark\"}";
        var old = new AppearancePreferences(false, BackdropMaterial.Acrylic, AppearanceTheme.Dark);
        var custom = old with { CustomBackdropEnabled = true, TintOpacity = 0.25, LuminosityOpacity = 0.7, TintColor = "#abcdEF" };
        var canonical = custom with { TintColor = "#ABCDEF" };
        var store = new AppearancePreferencesStore(Path.Combine(root, "appearance-parameters"));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(store.FilePath, oldJson);
        foreach (var invalid in new[] {
            custom with { TintOpacity = double.NaN }, custom with { TintOpacity = double.PositiveInfinity },
            custom with { TintOpacity = double.NegativeInfinity }, custom with { TintOpacity = -0.0001 }, custom with { TintOpacity = 1.0001 },
            custom with { LuminosityOpacity = double.NaN }, custom with { LuminosityOpacity = double.PositiveInfinity },
            custom with { LuminosityOpacity = double.NegativeInfinity }, custom with { LuminosityOpacity = -0.0001 }, custom with { LuminosityOpacity = 1.0001 },
            custom with { TintColor = "#80ABCDEF" }, custom with { TintColor = "#ABC" }, custom with { TintColor = "ABCDEF" },
            custom with { TintColor = " #ABCDEF" }, custom with { TintColor = "#ABCDEG" }, custom with { TintColor = "#ＡＢＣＤＥＦ" },
            custom with { TintColor = "" }, custom with { CustomBackdropEnabled = false, TintOpacity = double.NaN } })
        {
            bool rejected = false; try { store.Save(invalid); } catch (ArgumentException) { rejected = true; }
            check(rejected && File.ReadAllText(store.FilePath) == oldJson && !File.Exists(store.FilePath + ".bak"), "invalid in-memory material parameters preserve original appearance without backup/write");
            var missing = new AppearancePreferencesStore(Path.Combine(root, "invalid-appearance-target"));
            try { missing.Save(invalid); } catch (ArgumentException) { }
            check(!Directory.Exists(Path.GetDirectoryName(missing.FilePath)), "material validation precedes destination creation");
        }
        check(store.Load(out var warning) == old && warning is null && File.ReadAllText(store.FilePath) == oldJson,
            "strict V1 appearance migrates new defaults without rewriting old file");
        var defaults = new AppearancePreferences();
        check(!defaults.CustomBackdropEnabled && defaults.TintOpacity == 0.8 && defaults.LuminosityOpacity == 0.85 && defaults.TintColor is null,
            "reset defaults disable custom material with theme tint and bounded defaults");
        const string json = """
            {"Version":2,"BackdropEnabled":true,"Material":"Acrylic","Theme":"Dark",
             "CustomBackdropEnabled":true,"TintOpacity":0.25,"LuminosityOpacity":0.7,"TintColor":"#abcdEF"}
            """;
        using var document = JsonDocument.Parse(json);
        var appearance = SettingsJson.ReadAppearance(document.RootElement);
        check(appearance == canonical with { BackdropEnabled = true }, "V2 read retains every original and custom field while canonicalizing RGB");
        using var saved = JsonDocument.Parse(SettingsJson.AppearanceBytes(appearance));
        check(saved.RootElement.GetProperty("Version").GetInt32() == 2 &&
            saved.RootElement.GetProperty("TintColor").GetString() == "#ABCDEF", "appearance V2 parameters round trip with canonical RGB color");
        store.Save(custom);
        check(store.Load(out warning) == canonical && warning is null && File.ReadAllText(store.FilePath + ".bak") == oldJson && custom.TintColor == "#abcdEF",
            "V2 atomic save canonicalizes color, preserves old V1 backup and never mutates caller record");
        foreach (double boundary in new[] { 0d, 1d })
        {
            var value = custom with { TintOpacity = boundary, LuminosityOpacity = 1 - boundary, TintColor = null };
            store.Save(value);
            check(store.Load(out warning) == value && warning is null, "opacity endpoints and null theme color survive save/reload");
        }
        var valid = JsonNode.Parse(json)!.AsObject();
        var invalidJson = new List<string>();
        foreach (var field in valid.Select(pair => pair.Key).ToArray())
        {
            var missing = (JsonObject)valid.DeepClone(); missing.Remove(field); invalidJson.Add(missing.ToJsonString());
        }
        foreach (var (field, value) in new (string, JsonNode?)[] {
            ("Version", JsonValue.Create(3)), ("Version", JsonValue.Create("2")), ("CustomBackdropEnabled", JsonValue.Create(1)),
            ("TintOpacity", null), ("TintOpacity", JsonValue.Create("0.5")), ("TintOpacity", JsonValue.Create(-0.1)), ("TintOpacity", JsonValue.Create(1.1)),
            ("LuminosityOpacity", null), ("LuminosityOpacity", JsonValue.Create(true)), ("LuminosityOpacity", JsonValue.Create(-0.1)), ("LuminosityOpacity", JsonValue.Create(1.1)),
            ("TintColor", JsonValue.Create(123)), ("TintColor", JsonValue.Create("#80ABCDEF")), ("TintColor", JsonValue.Create("#ABCDEG")) })
        {
            var modified = (JsonObject)valid.DeepClone(); modified[field] = value; invalidJson.Add(modified.ToJsonString());
        }
        invalidJson.Add(json.Replace("\"TintOpacity\":0.25", "\"TintOpacity\":1e999"));
        invalidJson.Add(json.Replace("\"LuminosityOpacity\":0.7", "\"LuminosityOpacity\":1e999"));
        invalidJson.Add(json.Replace("\"TintOpacity\":0.25", "\"TintOpacity\":0.25,\"TintOpacity\":0.5"));
        invalidJson.Add(json.Replace("\"Version\":2", "\"Version\":1,\"Version\":2"));
        invalidJson.Add(json.Replace("\"Version\":2", "\"Unexpected\":true,\"Version\":2"));
        invalidJson.Add(oldJson.Replace("\"Version\":1", "\"Version\":1,\"TintOpacity\":0.5"));
        foreach (var text in invalidJson)
        {
            File.WriteAllText(store.FilePath, text);
            check(store.Load(out warning) == defaults && !string.IsNullOrWhiteSpace(warning) && File.ReadAllText(store.FilePath) == text,
                "invalid or ambiguous appearance is reported explicitly and preserved without silent partial defaults");
            bool rejected = false;
            try { using var invalid = JsonDocument.Parse(text); SettingsJson.ReadAppearance(invalid.RootElement); }
            catch (Exception error) when (error is InvalidDataException or JsonException) { rejected = true; }
            check(rejected, "bundle appearance parser rejects invalid or incomplete V2 and mixed V1 fields");
        }
        var bundle = new SettingsBundle(Array.Empty<ProcessKeeper.Core.WhitelistRule>(), custom, new());
        var export = Path.Combine(root, "custom-appearance-bundle.json");
        SettingsBundleStore.Export(export, bundle);
        var imported = SettingsBundleStore.ReadImport(export);
        check(imported.Appearance == canonical, "all-settings export/import preserves custom parameters and canonical color");
        using (var exported = JsonDocument.Parse(File.ReadAllText(export)))
            check(exported.RootElement.GetProperty("Version").GetInt32() == 1 && exported.RootElement.GetProperty("Appearance").GetProperty("Version").GetInt32() == 2,
                "outer backup schema stays compatible while appearance section explicitly upgrades to V2");
        var oldExport = JsonNode.Parse(File.ReadAllText(export))!.AsObject(); oldExport["Appearance"] = JsonNode.Parse(oldJson);
        var oldPath = Path.Combine(root, "old-appearance-bundle.json"); File.WriteAllText(oldPath, oldExport.ToJsonString());
        check(SettingsBundleStore.ReadImport(oldPath).Appearance == old, "old complete settings backups migrate appearance parameters to defaults");
        foreach (bool priorCustom in new[] { false, true })
        {
            var directory = Path.Combine(root, "custom-rollback-" + priorCustom);
            SettingsBundleStore.CommitAll(directory, priorCustom ? imported : bundle with { Appearance = old });
            if (!priorCustom) File.WriteAllText(Path.Combine(directory, "appearance.json"), oldJson);
            var files = new[] { "whitelist.json", "appearance.json", "view.json" };
            var before = files.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(directory, name)));
            try { SettingsBundleStore.CommitAll(directory, priorCustom ? bundle with { Appearance = defaults } : imported, index => { if (index == 2) throw new IOException("fixture: fail after appearance replacement"); }); throw new Exception("Expected rollback"); }
            catch (SettingsCommitException error)
            {
                check(error.RollbackSucceeded && files.All(name => File.ReadAllBytes(Path.Combine(directory, name)).SequenceEqual(before[name])), "failed bundle commit restores exact original settings bytes including material parameters: " + priorCustom);
                check(new AppearancePreferencesStore(error.BackupDirectory).Load(out warning) == (priorCustom ? canonical : old) && warning is null,
                    "transaction backup remains readable with complete V2 parameters or migrated V1 defaults: " + priorCustom);
            }
        }
    }
}
