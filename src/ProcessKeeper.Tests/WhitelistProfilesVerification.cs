using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class WhitelistProfilesVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "ProcessKeeper-profile-fixture-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "whitelist.json");
        var store = new WhitelistStore(root);
        var profiles = new WhitelistProfilesStore(root);
        bool Reject(Action action) { try { action(); return false; } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException) { return true; } }
        var original = new WhitelistRule { Id = "personal", Name = "Personal sample", Kind = RuleKind.ProcessName, Value = "personal.exe", IncludeDescendants = true };
        var other = original with { Id = "other", Name = "Other sample", Value = "other.exe", Enabled = false };
        try
        {
            var fresh = profiles.Load();
            check(fresh.Profiles.Count == 1 && fresh.ActiveRules.Count == 0 && store.Load().Count == 0 && !Directory.Exists(root), "new users have one virtual empty profile without creating files");
            store.Save(new[] { original });
            var legacyBytes = File.ReadAllBytes(path);
            var migrated = profiles.Load();
            check(migrated.ActiveRules.SequenceEqual(new[] { original }) && File.ReadAllBytes(path).SequenceEqual(legacyBytes), "loading a historical personal whitelist preserves every byte and rule");
            var second = profiles.Create("Second", migrated.Revision);
            var secondId = second.Profiles.Single(item => item.Name == "Second").Id;
            check(second.Profiles.Count == 2 && second.ActiveRules.SequenceEqual(new[] { original }) && second.Profiles.Single(item => item.Id == secondId).Rules.Count == 0, "creating a profile is empty and leaves the active personal rules intact");
            check(File.ReadAllBytes(path + ".bak").SequenceEqual(legacyBytes), "first catalogue migration keeps the exact prior document as backup");
            var edited = profiles.Save(secondId, new[] { other }, second.Revision);
            check(store.Load().SequenceEqual(new[] { original }) && profiles.Preview(secondId).Rules.SequenceEqual(new[] { other }), "editing an inactive profile does not change the active whitelist");
            check(Reject(() => profiles.Save(secondId, Array.Empty<WhitelistRule>(), second.Revision)), "stale editor revisions cannot overwrite newer rules");
            var activated = profiles.Activate(secondId, edited.Revision);
            check(activated.ActiveId == secondId && store.Load().SequenceEqual(new[] { other }) && profiles.Preview(migrated.ActiveId).Rules.SequenceEqual(new[] { original }), "atomic activation updates active rules and retains the previous profile");
            using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
            {
                var data = json.RootElement.GetProperty("Profiles");
                check(!data.GetProperty("Items").EnumerateArray().Single(item => item.GetProperty("Id").GetString() == secondId).TryGetProperty("Rules", out _), "the active profile has only one authoritative Rules array");
            }
            store.Save(new[] { original, other });
            var ordinary = profiles.Load();
            check(ordinary.ActiveId == secondId && ordinary.ActiveRules.Count == 2 && ordinary.Profiles.Count == 2 && ordinary.Revision != activated.Revision, "ordinary whitelist Save retains profiles and invalidates open editors");
            check(Reject(() => profiles.Activate(migrated.ActiveId, activated.Revision)), "switch with a stale ordinary-save revision is refused");
            var exported = Path.Combine(root, "portable.json");
            WhitelistStore.ExportToFile(exported, ordinary.ActiveRules);
            check(WhitelistStore.ReadImport(exported).SequenceEqual(ordinary.ActiveRules) && !File.ReadAllText(exported).Contains("Profiles"), "single-whitelist export remains portable and excludes every inactive profile");
            check(Reject(() => WhitelistStore.ReadImport(path)), "single-whitelist import explicitly refuses a profile catalogue");
            check(RuleFileCodec.Parse(RuleFileCodec.Serialize(new[] { original, other })).SequenceEqual(new[] { original, other }), "editor codec preserves IDs enabled flags and descendant flags exactly");
            foreach (var bad in new[] { "{", "{\"Version\":1,\"Version\":1,\"Rules\":[]}", "{\"Version\":1,\"Rules\":[],\"Extra\":0}", "{\"Version\":\"1\",\"Rules\":[]}", "{\"Version\":1,\"Rules\":[null]}" })
                check(Reject(() => RuleFileCodec.Parse(bad)), "editor rejects malformed, duplicate, unknown, mistyped or null fields");
            check(Reject(() => profiles.Save(secondId, new[] { original, original }, ordinary.Revision)), "duplicate rule IDs fail before changing a profile");
            check(Reject(() => profiles.Create(" second ", ordinary.Revision)) && Reject(() => profiles.Create("sEcOnD", ordinary.Revision)), "profile names reject surrounding whitespace and case-insensitive duplicates");
            check(Reject(() => profiles.Rename(secondId, "bad\nname", ordinary.Revision)), "profile names reject control characters");
            check(Reject(() => profiles.Delete(secondId, ordinary.Revision)), "the active profile cannot be deleted");
            var renamed = profiles.Rename(secondId, "Renamed", ordinary.Revision);
            check(renamed.ActiveProfile.Name == "Renamed" && renamed.ActiveRules.Count == 2, "renaming preserves active rules");
            for (var i = 3; i <= 5; i++) renamed = profiles.Create("Profile " + i, renamed.Revision);
            check(renamed.Profiles.Count == 5 && Reject(() => profiles.Create("Sixth", renamed.Revision)), "the migrated active profile counts toward the five-profile limit");
            var inactiveId = renamed.Profiles.Last().Id;
            var deleted = profiles.Delete(inactiveId, renamed.Revision);
            check(deleted.Profiles.Count == 4 && deleted.ActiveRules.Count == 2 && Reject(() => profiles.Preview(inactiveId)), "deleting an inactive profile preserves the active profile and rejects stale selection");
            var good = File.ReadAllBytes(path);
            using (WhitelistProfileOperationGate.EnterClose())
            {
                check(Reject(() => profiles.Activate(migrated.ActiveId, deleted.Revision)), "activation is refused throughout an active close lease");
                check(Reject(() => store.Save(Array.Empty<WhitelistRule>())), "ordinary whitelist save is also refused during closing");
            }
            check(File.ReadAllBytes(path).SequenceEqual(good), "rejected close-time changes preserve exact disk state");
            using (WhitelistProfileOperationGate.EnterMutation())
            {
                check(Reject(() => WhitelistProfileOperationGate.EnterClose()), "closing cannot start during a profile transaction");
                var captureCalled = false;
                check(Reject(() => new ProcessCloser(capture: () => { captureCalled = true; return ProcessSnapshot.Empty; })
                    .CloseDetailedAsync(Array.Empty<ProcessRecord>(), false, _ => new(false, "fixture"), ProcessSnapshot.Empty).GetAwaiter().GetResult()) && !captureCalled,
                    "the real close entry checks the transaction gate before any close work");
            }
            using (WhitelistProfileOperationGate.EnterClose()) { }
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(Reject(() => profiles.Activate(migrated.ActiveId, deleted.Revision)), "a locked destination rejects the atomic replacement");
            check(File.ReadAllBytes(path).SequenceEqual(good) && Directory.GetFiles(root, ".whitelist-*.tmp").Length == 0, "failed replacement leaves the original and no pending temporary document");
            var damaged = Encoding.UTF8.GetString(good).Replace("\"ActiveId\": \"" + secondId + "\"", "\"ActiveId\": \"absent\"");
            File.WriteAllText(path, damaged);
            check(Reject(() => profiles.Load()) && Reject(() => store.Load()) && Reject(() => store.Save(Array.Empty<WhitelistRule>())), "corrupt profile metadata closes both load and save paths even with valid root rules");
            check(File.ReadAllText(path) == damaged, "damaged metadata is never silently repaired or reset");
            File.WriteAllBytes(path, good);
            var large = original with { Name = new string('x', RuleFileCodec.MaximumFileBytes) };
            check(Reject(() => profiles.Save(secondId, new[] { large }, deleted.Revision)), "each profile retains the one-MiB rule-document limit");
            check(Reject(() => profiles.Save(secondId, Enumerable.Range(0, 1001).Select(i => original with { Id = i.ToString() }).ToArray(), deleted.Revision)), "each profile retains the 1000-rule limit");
            var tooMany = deleted with { Profiles = Array.AsReadOnly(Enumerable.Range(0, 6).Select(i => new WhitelistProfile("id" + i, "Name " + i, Array.Empty<WhitelistRule>())).ToArray()), ActiveId = "id0" };
            check(Reject(() => WhitelistProfilesStore.SerializeDocument(Array.Empty<WhitelistRule>(), tooMany)), "constructed import snapshots cannot bypass the five-profile limit");
            check(Reject(() => WhitelistProfilesStore.SerializeDocument(new[] { other }, deleted)), "all-settings export rejects inconsistent active rules and profile state");
            var only = new WhitelistProfilesStore(Path.Combine(root, "only"));
            var onlyState = only.Load();
            check(Reject(() => only.Delete(onlyState.ActiveId, onlyState.Revision)), "the last virtual profile cannot be deleted");
            var prepared = store.PrepareSave(Array.Empty<WhitelistRule>());
            var preparedProfiles = WhitelistProfilesStore.ReadProfiles(Encoding.UTF8.GetString(prepared));
            check(preparedProfiles is not null && preparedProfiles.ActiveRules.Count == 0 && preparedProfiles.Profiles.Count == deleted.Profiles.Count && File.ReadAllBytes(path).SequenceEqual(good), "bundle preparation replaces only active rules and performs no file writes");
            var activeJson = RuleFileCodec.Serialize(new[] { original });
            var invalidCatalogues = new[]
            {
                "{\"Version\":2,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\"}]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":null}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\",\"Rules\":[]}]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\"},{\"Id\":\"default\",\"Name\":\"Duplicate\"}]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\"},{\"Id\":\"other\",\"Name\":\"Other\",\"Rules\":[null]}]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\"},{\"Id\":\"other\",\"Name\":\"active\",\"Rules\":[]}]}",
                "{\"Version\":1,\"ActiveId\":\"default\",\"Items\":[{\"Id\":\"default\",\"Name\":\"Active\"}],\"Items\":[]}"
            };
            foreach (var invalidCatalogue in invalidCatalogues)
            {
                var invalidText = activeJson.TrimEnd().Substring(0, activeJson.TrimEnd().Length - 1) + ",\"Profiles\":" + invalidCatalogue + "}";
                check(Reject(() => WhitelistProfilesStore.ReadProfiles(invalidText)), "damaged catalogue shape, duplicate identity, duplicated rules or invalid inactive data is rejected");
            }
            var bomDirectory = Path.Combine(root, "bom"); Directory.CreateDirectory(bomDirectory);
            var bomPath = Path.Combine(bomDirectory, "whitelist.json");
            var bomBytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(activeJson)).ToArray();
            File.WriteAllBytes(bomPath, bomBytes);
            var bomStore = new WhitelistProfilesStore(bomDirectory); var bomState = bomStore.Load();
            check(bomState.ActiveRules.SequenceEqual(new[] { original }) && File.ReadAllBytes(bomPath).SequenceEqual(bomBytes), "a Windows-editor BOM is accepted without changing original bytes");
            bomStore.Create("Extra", bomState.Revision);
            check(File.ReadAllBytes(bomPath + ".bak").SequenceEqual(bomBytes), "BOM migration retains an exact backup including its original encoding prefix");
            check(Reject(() => WhitelistProfilesStore.ReadProfiles(new string(' ', WhitelistProfilesStore.MaximumFileBytes + 1))), "the complete catalogue input has a bounded five-MiB limit");
            var priorGoodBackup = File.ReadAllBytes(path + ".bak");
            File.WriteAllText(path, "{damaged catalogue");
            var rawDamaged = File.ReadAllBytes(path);
            using (WhitelistProfileOperationGate.EnterClose())
                check(Reject(() => profiles.ResetToDefaults()), "explicit recovery cannot run during a close operation");
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                check(Reject(() => profiles.ResetToDefaults()), "a locked corrupted file prevents reset without deleting it");
            check(File.ReadAllBytes(path).SequenceEqual(rawDamaged), "failed recovery retains exact damaged bytes for manual recovery");
            var recovered = profiles.ResetToDefaults();
            check(recovered.Snapshot.Profiles.Count == 1 && recovered.Snapshot.ActiveRules.Count == 0 && store.Load().Count == 0, "confirmed recovery replaces a damaged catalogue with one empty profile");
            check(recovered.BackupPath is not null && File.ReadAllBytes(recovered.BackupPath).SequenceEqual(rawDamaged) && File.ReadAllBytes(path + ".bak").SequenceEqual(priorGoodBackup), "recovery preserves raw damaged bytes separately without overwriting the preceding good backup");
            var newProfile = profiles.Create("Will reset", recovered.Snapshot.Revision);
            var allReset = profiles.ResetToDefaults();
            check(newProfile.Profiles.Count == 2 && allReset.Snapshot.Profiles.Count == 1 && allReset.BackupPath != recovered.BackupPath && File.Exists(recovered.BackupPath), "explicit reset clears all profiles and retains distinct backups across repeated resets");
        }
        finally
        {
            // This directory was uniquely allocated by this fixture; never select a production configuration path.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
