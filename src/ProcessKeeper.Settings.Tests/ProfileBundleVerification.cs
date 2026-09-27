using ProcessKeeper.App;
using ProcessKeeper.Core;

internal static class ProfileBundleVerification
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var source = Path.Combine(root, "profile-source");
        var active = new WhitelistRule { Id = "active", Name = "Active", Kind = RuleKind.Application, Value = "known:steam" };
        var other = active with { Id = "inactive", Name = "Inactive", Value = "known:qq" };
        var rules = new WhitelistStore(source); rules.Save(new[] { active });
        var store = new WhitelistProfilesStore(source);
        var state = store.Load(); state = store.Create("Other", state.Revision);
        var otherId = state.Profiles.Single(item => item.Name == "Other").Id;
        state = store.Save(otherId, new[] { other }, state.Revision);
        var bundle = new SettingsBundle(state.ActiveRules, new(), new(), Profiles: state);
        var path = Path.Combine(root, "all-profiles.json");
        SettingsBundleStore.Export(path, bundle);
        var loaded = SettingsBundleStore.ReadImport(path);
        check(loaded.Profiles is not null && loaded.Profiles.Profiles.Count == 2 && loaded.Profiles.ActiveId == state.ActiveId && loaded.Profiles.Profiles.Single(item => item.Id == otherId).Rules.SequenceEqual(new[] { other }), "all-settings backup preserves every profile and active selection");
        var destination = Path.Combine(root, "profile-destination");
        SettingsBundleStore.CommitAll(destination, loaded);
        var restored = new WhitelistProfilesStore(destination).Load();
        check(restored.Profiles.Count == 2 && restored.ActiveRules.SequenceEqual(new[] { active }) && restored.Profiles.Single(item => item.Id == otherId).Rules.SequenceEqual(new[] { other }), "all-settings restore installs the complete profile catalogue atomically with active rules");
        SettingsBundleStore.CommitAll(destination, bundle with { Rules = Array.Empty<WhitelistRule>(), Profiles = null });
        restored = new WhitelistProfilesStore(destination).Load();
        check(restored.ActiveRules.Count == 0 && restored.Profiles.Count == 2 && restored.Profiles.Single(item => item.Id == otherId).Rules.SequenceEqual(new[] { other }), "old settings imports replace only active rules without dropping inactive profiles");
        var before = File.ReadAllBytes(Path.Combine(destination, "whitelist.json"));
        try { SettingsBundleStore.CommitAll(destination, bundle, index => { if (index == 1) throw new IOException("injected profile transaction failure"); }); throw new Exception("failure injection not called"); }
        catch (SettingsCommitException ex) { check(ex.RollbackSucceeded && File.ReadAllBytes(Path.Combine(destination, "whitelist.json")).SequenceEqual(before), "ordinary settings failure restores exact catalogue and active rules together"); }
        var rejected = false;
        try { SettingsBundleStore.Export(path, bundle with { Rules = Array.Empty<WhitelistRule>() }); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "all-settings export rejects active rules inconsistent with the selected profile");
        using (WhitelistProfileOperationGate.EnterClose())
        {
            rejected = false;
            try { SettingsBundleStore.CommitAll(destination, bundle); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && File.ReadAllBytes(Path.Combine(destination, "whitelist.json")).SequenceEqual(before), "settings restore cannot race a close operation or change rules midway");
        }
        var sharing = state with { Profiles = Array.AsReadOnly(state.Profiles.Select(item => item with { Rules = RulePortability.ForSharing(item.Rules) }).ToArray()) };
        SettingsBundleStore.Export(path, bundle with { Rules = sharing.ActiveRules, Profiles = sharing });
        check(SettingsBundleStore.ReadImport(path).Profiles?.Profiles.Count == 2, "shareable profile snapshots preserve all profile identities and active selection");
        var damaged = File.ReadAllText(path).Replace("\"ActiveId\": \"default\"", "\"ActiveId\": \"missing\"");
        File.WriteAllText(path, damaged);
        rejected = false; try { SettingsBundleStore.ReadImport(path); } catch (InvalidDataException) { rejected = true; }
        check(rejected && File.ReadAllBytes(Path.Combine(destination, "whitelist.json")).SequenceEqual(before), "damaged imported profile metadata is rejected before modifying the destination");
    }
}
