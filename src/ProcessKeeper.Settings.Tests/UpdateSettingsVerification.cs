using System.Text.Json.Nodes;
using ProcessKeeper.App;
using ProcessKeeper.Core;

internal static class UpdateSettingsVerification
{
    internal static void Run(string root, SettingsBundle original, SettingsBundle next, Action<bool,string> check)
    {
        var prefs = new UpdatePreferences("KangQiovo/ProcessKeeper", false, UpdateSourceMode.ThirdParty, "ghproxy", false);
        var modern = next with { Updates = prefs };
        var file = Path.Combine(root, "all-with-updates.json");
        SettingsBundleStore.Export(file, modern);
        check(SettingsBundleStore.ReadImport(file).Updates == prefs, "all-settings export includes every update preference");
        var current = File.ReadAllText(file);
        var foreign = JsonNode.Parse(current)!.AsObject();
        foreign["Updates"]!["Repository"] = "OtherOwner/ProcessKeeper";
        File.WriteAllText(file, foreign.ToJsonString());
        var rejectedRepository = false;
        try { SettingsBundleStore.ReadImport(file); } catch (InvalidDataException) { rejectedRepository = true; }
        check(rejectedRepository, "a complete otherwise-valid settings bundle cannot replace the fixed update repository");
        foreach (var invalid in new[] { "null", "{}", "{\"Version\":1,\"Repository\":\"https://evil.example/a\"}" })
        {
            var json = JsonNode.Parse(current)!.AsObject(); json["Updates"] = JsonNode.Parse(invalid);
            File.WriteAllText(file, json.ToJsonString());
            var rejected = false; try { SettingsBundleStore.ReadImport(file); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid optional update section rejects the whole settings import");
        }
        var directory = Path.Combine(root, "update-preservation");
        SettingsBundleStore.CommitAll(directory, modern);
        var prior = File.ReadAllText(Path.Combine(directory,"update.json"));
        SettingsBundleStore.CommitAll(directory, original);
        check(File.ReadAllText(Path.Combine(directory,"update.json")) == prior, "importing a pre-update settings file preserves existing update choices");
        var names = new[] { "whitelist.json", "appearance.json", "view.json", "update.json" };
        for (var failAt = 0; failAt < 4; failAt++)
        foreach (var existing in new[] { false, true })
        {
            var target = Path.Combine(root, "update-rollback-" + failAt + existing);
            if(existing) SettingsBundleStore.CommitAll(target, original with { Updates = new UpdatePreferences() });
            var before = names.ToDictionary(n => n, n => File.Exists(Path.Combine(target,n)) ? File.ReadAllText(Path.Combine(target,n)) : null);
            var failed = false;
            try { SettingsBundleStore.CommitAll(target, modern, i => { if(i==failAt) throw new IOException("injected update transaction failure"); }); }
            catch(SettingsCommitException ex) { failed = ex.RollbackSucceeded; }
            check(failed && names.All(n => before[n] is null ? !File.Exists(Path.Combine(target,n)) : File.ReadAllText(Path.Combine(target,n))==before[n]),
                "four-file transaction restores every original file or absence at replacement " + failAt + " existing=" + existing);
        }
    }
}
