using ProcessKeeper.Core;

L.Language = "zh-Hans";
var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    count++; Console.WriteLine("PASS: " + name);
}
AutorunSafetyVerification.Run(Check);
AutorunAdvancedVerification.Run(Check);
AutorunApprovalVerification.Run(Check);
AutorunOwnershipVerification.Run(Check);
count += AutorunSearchVerification.Run();
var fixtureRoot = Path.Combine(Environment.GetEnvironmentVariable("PROCESSKEEPER_AUTORUN_TEST_ROOT") ?? Path.GetTempPath(), "autorun-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
var store = new AutorunBackupStore(fixtureRoot);
var locator = new AutorunLocator { Hive = "HKCU", View = 32, Path = @"Software\Microsoft\Windows\CurrentVersion\RunOnce", ValueName = "!*FixtureOnly" };
var original = new AutorunState(true, true, "{\"Kind\":2,\"Value\":\"%LOCALAPPDATA%\\\\Fixture\\\\tool.exe --safe\"}");
var entry = new AutorunEntry { Id = AutorunIdentity.Id(AutorunSourceKind.RegistryRunOnce, locator), SourceKind = AutorunSourceKind.RegistryRunOnce, Locator = locator,
    Name = "!*FixtureOnly", CanChange = true, Enabled = true, Fingerprint = original.Fingerprint };
var record = new AutorunBackupRecord(entry, original, new(false, false, ""));
var backupPath = store.Save(record);
Check(File.Exists(backupPath), "isolated backup is persisted before any backend write");
Check(store.Load(entry.Id) == record, "backup round-trip preserves exact raw payload, RunOnce flags and registry view");
Check(store.ReadAll().Count == 1, "isolated managed backups can be enumerated for disabled-entry restore");
Check(!Directory.EnumerateFiles(fixtureRoot, "*.tmp").Any(), "atomic backup save does not leave temporary files");
bool refused = false;
try { store.Load("..\\unrelated"); } catch (ArgumentException) { refused = true; }
Check(refused, "backup identifier traversal is rejected before any file read");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
refused = false;
try { new AutorunCatalog(fixtureRoot).Scan(cancelled.Token); } catch (OperationCanceledException) { refused = true; }
Check(refused, "pre-canceled catalog does not call any native inventory source");
L.Language = "en";
Check(L.T("已启用计划任务；不会立即运行任务。") == "The scheduled task was enabled. It will not be run now.", "backend English catalog loads without conflicting translations");
L.Language = "zh-Hant";
Check(L.T("自动启动") == "自動啟動", "backend Traditional Chinese labels cover startup state");
L.Language = "zh-Hans";
if (args.Contains("--read-only-live", StringComparer.Ordinal))
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var snapshot = new AutorunCatalog(fixtureRoot).Scan();
    Check(snapshot.Entries.Count <= AutorunCatalog.MaximumEntries, "live inventory respects the total entry bound");
    Check(snapshot.Entries.Select(item => item.Id).Distinct().Count() == snapshot.Entries.Count, "live native identities are distinct");
    Check(snapshot.Entries.Where(item => item.IsSystem).All(item => !item.CanChange), "system entries remain read-only in live inventory");
    Console.WriteLine("LIVE COUNTS ONLY: " + string.Join(" | ", snapshot.Entries.GroupBy(item => item.SourceKind).Select(group => group.Key + "=" + group.Count())));
    Console.WriteLine($"LIVE: entries={snapshot.Entries.Count}; warnings={snapshot.Warnings.Count}; truncated={snapshot.Truncated}; seconds={watch.Elapsed.TotalSeconds:F2}");
}
Console.WriteLine($"PASS: {count} autorun assertions. All changes used memory doubles or isolated fixture files; no real startup setting was changed.");
