using System.Text.Json.Nodes;
using ProcessKeeper.App;
using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

if (args.Length > 0 && (args.Length != 2 || args[0] != "--output-root"))
    throw new ArgumentException("Usage: ProcessKeeper.Settings.Tests [--output-root <scratch-directory>]");
var fixtureBase = args.Length == 2 ? args[1] : Environment.GetEnvironmentVariable("PROCESSKEEPER_SETTINGS_TEST_ROOT");
fixtureBase = string.IsNullOrWhiteSpace(fixtureBase) ? Path.Combine(Path.GetTempPath(), "ProcessKeeper.Settings.Tests") : Path.GetFullPath(fixtureBase);
var root = Path.Combine(fixtureBase, "settings-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var evidence = new StreamWriter(Path.Combine(root, "verification-report.txt")) { AutoFlush = true };
void Report(string text) { Console.WriteLine(text); evidence.WriteLine(text); }
Report("Fixture root: " + root);
var count = 0;
void Check(bool value, string name) { if (!value) { Report("FAIL: " + name); throw new Exception(name); } count++; Report("PASS: " + name); }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("accepted invalid data: " + name); }
var oldBundle = new SettingsBundle(WhitelistStore.CreateDefaults(), new AppearancePreferences(), new ViewPreferences());
AppearanceParametersVerification.Run(root, Check);
ProfileBundleVerification.Run(root, Check);
var defaultExample = SettingsBundleStore.ReadImport(Path.Combine(AppContext.BaseDirectory, "fixtures-input", "default-settings.json"));
Check(defaultExample.Rules.SequenceEqual(oldBundle.Rules) && defaultExample.Appearance == oldBundle.Appearance && defaultExample.View == oldBundle.View, "distributed default settings example exactly matches product defaults");
var sourceRules = new List<WhitelistRule> { new() { Id = "fixture-local", Name = "Local fixture", Kind = RuleKind.ExecutablePath, Value = @"D:\Fixture\program.exe", Enabled = false, IncludeDescendants = true } };
var newBundle = new SettingsBundle(sourceRules, new(false, BackdropMaterial.Acrylic, AppearanceTheme.Dark), new(false, true, 4, 3, "zh-Hant", HistoryAutoScroll: false, InstalledDrive: "E:"));
var export = Path.Combine(root, "settings.json");
SettingsBundleStore.Export(export, oldBundle);
var previous = File.ReadAllText(export);
SettingsBundleStore.Export(export, newBundle);
Check(File.ReadAllText(export + ".bak") == previous, "export retains previous file backup");
var current = File.ReadAllText(export);
var loaded = SettingsBundleStore.ReadImport(export);
Check(loaded.Rules.SequenceEqual(newBundle.Rules) && loaded.Appearance == newBundle.Appearance && loaded.View == newBundle.View, "all settings round trip");
Check(!loaded.View.HistoryAutoScroll && loaded.View.Language == "zh-Hant", "all-settings round trip preserves manual scroll and selected language together");
Check(loaded.View.InstalledDrive == "E:", "all-settings round trip preserves selected drive");
var legacyExport = Path.Combine(root, "legacy-settings.json");
var noDrive = JsonNode.Parse(current)!.AsObject();
noDrive["View"]!.AsObject().Remove("InstalledDrive");
File.WriteAllText(legacyExport, noDrive.ToJsonString());
Check(SettingsBundleStore.ReadImport(legacyExport).View == newBundle.View with { InstalledDrive = "" },
    "older settings missing drive default to all drives without changing language or scroll");
foreach (var keepLanguage in new[] { false, true })
{
    var legacy = JsonNode.Parse(current)!.AsObject();
    legacy["View"]!.AsObject().Remove("HistoryAutoScroll");
    if (!keepLanguage) legacy["View"]!.AsObject().Remove("Language");
    File.WriteAllText(legacyExport, legacy.ToJsonString());
    var legacyView = SettingsBundleStore.ReadImport(legacyExport).View;
    Check(legacyView.HistoryAutoScroll, "older all-settings file defaults to automatic history scrolling");
    Check(legacyView.Language == (keepLanguage ? "zh-Hant" : "auto"), "older all-settings file keeps optional language without losing scroll default");
    Check(legacyView with { HistoryAutoScroll = false, Language = "zh-Hant" } == newBundle.View, "older settings retain every existing view option");
}
Check(sourceRules[0].Value == @"D:\Fixture\program.exe" && !sourceRules[0].Enabled, "export preserves local rule identity and disabled state");
Check(!current.Contains("activity.log") && !current.Contains("OwnerSid") && !current.Contains("Runtime"), "bundle contains configuration fields only");

var invalid = new List<string> { "null", "[]", "{}", "{", new string(' ', 6 * 1024 * 1024 + 1), "{\"Version\":1,\"Rules\":[]}" };
void Mutate(Action<JsonObject> mutate) { var json = JsonNode.Parse(current)!.AsObject(); mutate(json); invalid.Add(json.ToJsonString()); }
foreach (var field in new[] { "Format", "Version", "Whitelist", "Appearance", "View" }) { Mutate(j => j.Remove(field)); Mutate(j => j[field] = null); }
Mutate(j => j["Format"] = "AnotherApp.Settings");
Mutate(j => j["Version"] = 2);
Mutate(j => j["Version"] = "1");
Mutate(j => j["UnexpectedPrivateData"] = "must not import");
foreach (var section in new[] { "Whitelist", "Appearance", "View" })
{
    Mutate(j => j[section]!["Version"] = section == "Appearance" ? 3 : 2);
    Mutate(j => j[section]!["Version"] = null);
    Mutate(j => j[section]!["Extra"] = true);
}
foreach (var field in new[] { "BackdropEnabled", "Material", "Theme" }) { Mutate(j => j["Appearance"]!.AsObject().Remove(field)); Mutate(j => j["Appearance"]![field] = null); }
Mutate(j => j["Appearance"]!["Material"] = "0");
Mutate(j => j["Appearance"]!["Material"] = 1);
Mutate(j => j["Appearance"]!["Theme"] = "Neon");
foreach (var field in new[] { "LiveRefresh", "ShowSystemProcesses", "CategoryFilter", "SortOrder" }) { Mutate(j => j["View"]!.AsObject().Remove(field)); Mutate(j => j["View"]![field] = null); }
Mutate(j => j["View"]!["LiveRefresh"] = "false");
Mutate(j => j["View"]!["HistoryAutoScroll"] = "false");
Mutate(j => j["View"]!["HistoryAutoScroll"] = null);
Mutate(j => j["View"]!["HistoryAutoScroll"] = 1);
foreach (var drive in new string?[] { null, "e:", "E:\\", "E:/Apps", "relative", "1:", "\\\\server\\share" })
    Mutate(j => j["View"]!["InstalledDrive"] = drive);
Mutate(j => j["View"]!["InstalledDrive"] = true);
Mutate(j => j["View"]!["CategoryFilter"] = 5);
Mutate(j => j["View"]!["CategoryFilter"] = -1);
Mutate(j => j["View"]!["SortOrder"] = 4);
Mutate(j => j["View"]!["SortOrder"] = -1);
Mutate(j => j["View"]!["SortOrder"] = 0.5);
Mutate(j => j["View"]!["CategoryFilter"] = long.MaxValue);
Mutate(j => j["View"]!["ShowSystemProcesses"] = false);
Mutate(j => j["View"]!["ShowSystemProcesses"] = 1);
Mutate(j => j["Appearance"]!["Material"] = "mica");
Mutate(j => j["Appearance"]!["Theme"] = "dark");
Mutate(j => j["Appearance"]!["BackdropEnabled"] = "false");
Mutate(j => j["Whitelist"]!["Rules"] = null);
Mutate(j => j["Whitelist"]!["Rules"] = new JsonArray((JsonNode?)null));
Mutate(j => j["Whitelist"]!["Rules"]!.AsArray().Add(j["Whitelist"]!["Rules"]![0]!.DeepClone()));
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Id"] = "");
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Value"] = "relative.exe");
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Enabled"] = null);
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Extra"] = true);
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Kind"] = "executablepath");
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Kind"] = 1);
Mutate(j => j["Whitelist"]!["Rules"]![0]!["IncludeDescendants"] = "true");
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Name"] = null);
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Id"] = null);
Mutate(j => j["Whitelist"]!["Rules"]![0]!["Value"] = null);
Mutate(j => j["Whitelist"]!["Rules"]![0]!.AsObject().Remove("IncludeDescendants"));
invalid.Add(current.Replace("\"Format\":", "\"Version\": 1, \"Format\":"));
invalid.Add(current.Replace("\"Theme\":", "\"Material\": \"Mica\", \"Theme\":"));
invalid.Add(current.Replace("\"LiveRefresh\":", "\"SortOrder\": 0, \"LiveRefresh\":"));
invalid.Add(current.Replace("\"HistoryAutoScroll\":", "\"HistoryAutoScroll\": true, \"HistoryAutoScroll\":"));
invalid.Add(current.Replace("\"InstalledDrive\":", "\"InstalledDrive\": \"C:\", \"InstalledDrive\":"));
invalid.Add(current.Replace("\"Name\":", "\"Id\": \"duplicate\", \"Name\":"));
invalid.Add(current.Replace("\"Rules\":", "\"Version\": 1, \"Rules\":"));
var badFile = Path.Combine(root, "invalid.json");
foreach (var content in invalid)
{
    File.WriteAllText(badFile, content);
    Reject(() => SettingsBundleStore.ReadImport(badFile), "invalid bundle is rejected");
    Check(File.ReadAllText(badFile) == content, "invalid import file remains unchanged");
}

var viewStore = new ViewPreferencesStore(Path.Combine(root, "view-only"));
Check(viewStore.Load() == new ViewPreferences() && !File.Exists(viewStore.FilePath), "missing view uses defaults without writes");
viewStore.Save(newBundle.View);
Check(viewStore.Load() == newBundle.View, "view preferences round trip");
var originalView = File.ReadAllText(viewStore.FilePath);
var preScrollView = JsonNode.Parse(originalView)!.AsObject();
preScrollView.Remove("HistoryAutoScroll");
File.WriteAllText(viewStore.FilePath, preScrollView.ToJsonString());
Check(viewStore.Load() == newBundle.View with { HistoryAutoScroll = true }, "existing view file uses auto-scroll without changing language or other options");
Check(File.ReadAllText(viewStore.FilePath) == preScrollView.ToJsonString(), "loading an older view file does not rewrite it");
foreach (var invalidScroll in new[] { "null", "\"false\"", "0", "{}", "[]" })
{
    var badView = JsonNode.Parse(originalView)!.AsObject();
    badView["HistoryAutoScroll"] = JsonNode.Parse(invalidScroll);
    File.WriteAllText(viewStore.FilePath, badView.ToJsonString());
    Reject(() => viewStore.Load(), "non-boolean history auto-scroll is rejected");
}
viewStore.Save(oldBundle.View);
Check(File.Exists(viewStore.FilePath + ".bak"), "view changes retain backup");
File.WriteAllText(viewStore.FilePath, "{}");
Reject(() => viewStore.Load(), "damaged view is reported to caller");
Reject(() => viewStore.Save(new ViewPreferences(SortOrder: 99)), "invalid view save rejected");
Check(File.ReadAllText(viewStore.FilePath) == "{}", "rejected view save leaves original unchanged");
var appearanceStore = new AppearancePreferencesStore(Path.Combine(root, "strict-appearance"));
Check(appearanceStore.Load(out var appearanceWarning) == new AppearancePreferences(), "missing appearance uses defaults");
Check(appearanceWarning is null && !Directory.Exists(Path.GetDirectoryName(appearanceStore.FilePath)), "initial appearance load does not write files");
appearanceStore.Save(newBundle.Appearance);
Check(appearanceStore.Load(out appearanceWarning) == newBundle.Appearance && appearanceWarning is null, "custom appearance settings round trip");
var originalAppearance = File.ReadAllText(appearanceStore.FilePath);
appearanceStore.Save(new AppearancePreferences(true, BackdropMaterial.Mica, AppearanceTheme.Light));
Check(File.ReadAllText(appearanceStore.FilePath + ".bak") == originalAppearance, "appearance replacement retains exact previous file");
Check(appearanceStore.Load(out appearanceWarning).Theme == AppearanceTheme.Light, "appearance replacement contains latest theme");
foreach (var content in new[] {
    "{", "null", "{}", "[]",
    "{\"Version\":2,\"BackdropEnabled\":true,\"Material\":\"Mica\",\"Theme\":\"System\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":\"Glass\",\"Theme\":\"System\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":0,\"Theme\":\"System\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":\"0\",\"Theme\":\"System\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":\"Mica\",\"Theme\":\"Neon\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":\"Mica\",\"Theme\":\"2\"}",
    "{\"Version\":1,\"BackdropEnabled\":\"true\",\"Material\":\"Mica\",\"Theme\":\"System\"}",
    "{\"Version\":1,\"Material\":\"Mica\",\"Theme\":\"System\"}",
    "{\"Version\":1,\"BackdropEnabled\":true,\"Material\":\"Mica\"}",
    new string(' ', 16 * 1024 + 1),
    originalAppearance.Replace("\"Theme\":", "\"Material\": \"Mica\", \"Theme\":"),
    originalAppearance.Replace("\"Version\":", "\"Extra\": true, \"Version\":") })
{
    File.WriteAllText(appearanceStore.FilePath, content);
    Check(appearanceStore.Load(out var warning) == new AppearancePreferences() && !string.IsNullOrWhiteSpace(warning), "ambiguous appearance file is not silently accepted");
    Check(File.ReadAllText(appearanceStore.FilePath) == content, "ambiguous appearance file remains unchanged");
}
appearanceStore.Save(newBundle.Appearance);
var validAppearance = File.ReadAllText(appearanceStore.FilePath);
try { appearanceStore.Save(newBundle.Appearance with { Theme = (AppearanceTheme)99 }); throw new Exception("invalid theme was accepted"); }
catch (ArgumentException) { Check(File.ReadAllText(appearanceStore.FilePath) == validAppearance, "invalid theme preserves active appearance file"); }
try { appearanceStore.Save(newBundle.Appearance with { Material = (BackdropMaterial)99 }); throw new Exception("invalid material was accepted"); }
catch (ArgumentException) { Check(File.ReadAllText(appearanceStore.FilePath) == validAppearance, "invalid material preserves active appearance file"); }
appearanceStore.Save(new AppearancePreferences());
Check(appearanceStore.Load(out appearanceWarning) == new AppearancePreferences() && appearanceWarning is null, "explicit appearance reset restores defaults");
Check(!Directory.EnumerateFiles(Path.GetDirectoryName(appearanceStore.FilePath)!, "*.tmp").Any(), "appearance saves leave no temporary files");
var ruleOnlyPath = Path.Combine(root, "rules-only.json");
WhitelistStore.ExportToFile(ruleOnlyPath, oldBundle.Rules);
Reject(() => SettingsBundleStore.ReadImport(ruleOnlyPath), "rule-only document is rejected by all-settings importer");
Reject(() => WhitelistStore.ReadImport(export), "full settings document is rejected by rule-only importer");

string[] files = ["whitelist.json", "appearance.json", "view.json"];
var success = Path.Combine(root, "success");
SettingsBundleStore.CommitAll(success, oldBundle);
var before = files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(success, f)));
SettingsBundleStore.CommitAll(success, newBundle);
Check(new WhitelistStore(success).Load().SequenceEqual(newBundle.Rules), "commit updates whitelist");
Check(new AppearancePreferencesStore(success).Load(out _) == newBundle.Appearance, "commit updates appearance");
Check(new ViewPreferencesStore(success).Load() == newBundle.View, "commit updates view");
foreach (var file in files)
    Check(Directory.EnumerateFiles(Path.Combine(success, "backups"), file, SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == before[file]), "commit retains exact original " + file);

for (var mask = 0; mask < 8; mask++)
for (var failAt = 0; failAt < 3; failAt++)
{
    var directory = Path.Combine(root, $"rollback-{mask}-{failAt}");
    Directory.CreateDirectory(directory);
    for (var i = 0; i < files.Length; i++) if ((mask & (1 << i)) != 0) File.WriteAllText(Path.Combine(directory, files[i]), i == 0 ? RuleFileCodec.Serialize(oldBundle.Rules) : "old-" + files[i]);
    var fail = failAt;
    try { SettingsBundleStore.CommitAll(directory, newBundle, i => { if (i == fail) throw new IOException("fixture failure"); }); throw new Exception("fixture did not fail"); }
    catch (SettingsCommitException ex) { Check(ex.RollbackSucceeded && Directory.Exists(ex.BackupDirectory), "failed import reports successful rollback and backup"); }
    for (var i = 0; i < files.Length; i++)
    {
        var path = Path.Combine(directory, files[i]);
        Check((mask & (1 << i)) != 0 ? File.ReadAllText(path) == (i == 0 ? RuleFileCodec.Serialize(oldBundle.Rules) : "old-" + files[i]) : !File.Exists(path), "rollback restores original content or absence");
    }
    Check(!Directory.EnumerateDirectories(directory, ".settings-stage-*").Any(), "rollback removes only staged temporary files");
}
var locked = Path.Combine(root, "locked");
SettingsBundleStore.CommitAll(locked, oldBundle);
using (var handle = new FileStream(Path.Combine(locked, "view.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
{
    try { SettingsBundleStore.CommitAll(locked, newBundle); throw new Exception("lock did not block replace"); }
    catch (SettingsCommitException ex) { Check(ex.RollbackSucceeded, "real IO failure rolls back earlier files"); }
}
Check(new WhitelistStore(locked).Load().SequenceEqual(oldBundle.Rules) && new AppearancePreferencesStore(locked).Load(out _) == oldBundle.Appearance && new ViewPreferencesStore(locked).Load() == oldBundle.View, "real IO failure leaves all original settings");
var rejectedTarget = Path.Combine(root, "invalid-target");
Reject(() => SettingsBundleStore.CommitAll(rejectedTarget, newBundle with { View = new ViewPreferences(CategoryFilter: 99) }), "invalid commit is rejected before writes");
Check(!Directory.Exists(rejectedTarget), "invalid commit does not create target directory");
Reject(() => SettingsBundleStore.Export(export, newBundle with { Rules = [sourceRules[0] with { Value = "not-full-path" }] }), "invalid export rejected");
Check(File.ReadAllText(export) == current, "invalid export preserves previous export");
foreach (var invalidBundle in new SettingsBundle[] {
    null!, newBundle with { Rules = null! }, newBundle with { Appearance = null! },
    newBundle with { View = null! }, newBundle with { Appearance = new AppearancePreferences(Theme: (AppearanceTheme)999) },
    newBundle with { Rules = [null!] }, newBundle with { View = new ViewPreferences(false, false, 4, 0) } })
{
    Reject(() => SettingsBundleStore.Export(export, invalidBundle), "invalid in-memory settings snapshot rejected before export");
    Check(File.ReadAllText(export) == current, "invalid in-memory snapshot preserves exported file");
}
var preBackup = Path.Combine(root, "pre-backup");
SettingsBundleStore.CommitAll(preBackup, oldBundle);
var originalFiles = files.ToDictionary(f => f, f => File.ReadAllText(Path.Combine(preBackup, f)));
SettingsBundleStore.CommitAll(preBackup, newBundle, i =>
{
    if (i != 0) return;
    var completeBackup = Directory.EnumerateDirectories(Path.Combine(preBackup, "backups"))
        .Single(d => files.All(f => File.Exists(Path.Combine(d, f))));
    Check(File.Exists(Path.Combine(completeBackup, "backup-info.json")), "backup manifest exists before first destination replacement");
    Check(files.All(f => File.ReadAllText(Path.Combine(completeBackup, f)) == originalFiles[f]), "all exact original contents are backed up before first replacement");
    Check(files.All(f => File.ReadAllText(Path.Combine(preBackup, f)) == originalFiles[f]), "all active files remain unchanged during backup stage");
});
var failedBackup = Path.Combine(root, "failed-backup");
SettingsBundleStore.CommitAll(failedBackup, oldBundle);
using (var handle = new FileStream(Path.Combine(failedBackup, "view.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    try { SettingsBundleStore.CommitAll(failedBackup, newBundle); throw new Exception("exclusive lock did not block backup"); }
    catch (SettingsCommitException ex) { Check(ex.RollbackSucceeded, "backup failure occurs before any active-file mutation"); }
}
Check(new WhitelistStore(failedBackup).Load().SequenceEqual(oldBundle.Rules) &&
    new AppearancePreferencesStore(failedBackup).Load(out _) == oldBundle.Appearance &&
    new ViewPreferencesStore(failedBackup).Load() == oldBundle.View, "failed backup leaves every original setting unchanged");
var blockedDestination = Path.Combine(root, "blocked-destination");
Directory.CreateDirectory(Path.Combine(blockedDestination, "view.json"));
File.WriteAllText(Path.Combine(blockedDestination, "whitelist.json"), RuleFileCodec.Serialize(oldBundle.Rules));
try { SettingsBundleStore.CommitAll(blockedDestination, newBundle); throw new Exception("directory conflict was accepted"); }
catch (SettingsCommitException ex) { Check(ex.RollbackSucceeded, "directory at a configuration filename prevents commit before mutations"); }
Check(File.ReadAllText(Path.Combine(blockedDestination, "whitelist.json")) == RuleFileCodec.Serialize(oldBundle.Rules) &&
    !File.Exists(Path.Combine(blockedDestination, "appearance.json")) &&
    Directory.Exists(Path.Combine(blockedDestination, "view.json")), "directory conflict preserves content and original absence");
var failedRollback = Path.Combine(root, "failed-rollback");
SettingsBundleStore.CommitAll(failedRollback, oldBundle);
FileStream? rollbackBlocker = null;
try
{
    SettingsBundleStore.CommitAll(failedRollback, newBundle, i =>
    {
        if (i != 1) return;
        rollbackBlocker = new FileStream(Path.Combine(failedRollback, "whitelist.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        throw new IOException("fixture: fail after first replacement and lock its rollback");
    });
    throw new Exception("rollback blocker did not fail");
}
catch (SettingsCommitException ex)
{
    Check(!ex.RollbackSucceeded, "rollback failure is explicitly reported to the caller");
    Check(new WhitelistStore(ex.BackupDirectory).Load().SequenceEqual(oldBundle.Rules) &&
        new AppearancePreferencesStore(ex.BackupDirectory).Load(out _) == oldBundle.Appearance &&
        new ViewPreferencesStore(ex.BackupDirectory).Load() == oldBundle.View, "rollback failure still retains a complete readable backup");
}
finally { rollbackBlocker?.Dispose(); }
UpdateSettingsVerification.Run(root, oldBundle, newBundle, Check);
ProfileSyncPreferencesVerification.Run(root, Check);
PerformanceSettingsVerification.Run(root, oldBundle, Check);
WhitelistScopeVerification.Run(root, oldBundle, Check);
BuildInfoVerification.Run(Check);
RuleMatcherVerification.Run(Check);
Report($"PASS: {count} settings assertions; only scratch fixtures were written.");
