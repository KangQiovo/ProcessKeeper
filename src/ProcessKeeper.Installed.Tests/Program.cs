using System.Diagnostics;
using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

var baseRoot = Environment.GetEnvironmentVariable("PROCESSKEEPER_INSTALLED_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "ProcessKeeper.Installed.Tests");
var root = Path.Combine(Path.GetFullPath(baseRoot), "installed-fixture-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var evidence = new StreamWriter(Path.Combine(root, "verification-report.txt")) { AutoFlush = true };
void Report(string message) { Console.WriteLine(message); evidence.WriteLine(message); }
int assertions = 0, skipped = 0;
void Check(bool condition, string name) { if (!condition) { Report("FAIL: " + name); throw new Exception(name); } assertions++; Report("PASS: " + name); }
string Entry(string relative)
{
    var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, []); return path;
}
Report("Fixture root: " + root);
InstalledSearchVerification.Run(Check, Report);
ApplicationGroupingVerification.Run(Check);
InstalledPackageCatalogExternalLocationTests.Run(Check);
InstalledExecutableIdentityVerification.Run(Check);
var catalog = new InstalledApplicationCatalog();
var alpha = Entry(@"VersionA\FixtureApp\alpha.exe");
var beta = Entry(@"VersionA\FixtureApp\helpers\beta.exe");
var gamma = Entry(@"VersionA\FixtureApp\helpers\nested\gamma.exe");
var tooDeep = Entry(@"VersionA\FixtureApp\helpers\nested\deeper\not-discovered.exe");
var nonExecutable = Entry(@"VersionA\FixtureApp\not-executable.dll");
var scan = catalog.ScanDirectory(Path.GetDirectoryName(alpha)!)!;
Check(scan is not null && scan.Executables.Count == 3, "manual directory finds executable components through two levels only");
Check(scan!.Executables.All(entry => !entry.Path.Equals(tooDeep, StringComparison.OrdinalIgnoreCase)), "deeper files are not traversed");
Check(scan.Executables.All(entry => !entry.Path.Equals(nonExecutable, StringComparison.OrdinalIgnoreCase)), "non-executable files are excluded");
Check(scan.Executables.All(entry => entry.ApplicationKey.StartsWith("path:", StringComparison.Ordinal)), "unrecognized executables keep exact path identities");
Check(scan.IdentityEvidence.Contains("最多扫描") && scan.IdentityEvidence.Contains("不保证"), "scan evidence explains limited discovery and existence-only meaning");
Check(catalog.ScanDirectory(Path.GetDirectoryName(alpha)!)!.Id == scan.Id, "the same installation gets a stable ID");
var otherVersion = Entry(@"VersionB\FixtureApp\alpha.exe");
var otherScan = catalog.ScanDirectory(Path.GetDirectoryName(otherVersion)!)!;
Check(otherScan.Name == scan.Name && otherScan.Id != scan.Id, "identically named versions under different directories remain separate");
var rules = InstalledApplicationCatalog.GetAppRules(scan);
Check(rules.Count == 3 && rules.All(rule => rule.Kind == RuleKind.ExecutablePath), "ordinary app produces exact executable rules only");
Check(rules.Select(rule => rule.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rules.Count, "component rules are distinct");
Check(rules.All(rule => !rule.IncludeDescendants), "adding an installed app does not implicitly keep descendants");
Check(rules.All(rule => Path.IsPathFullyQualified(rule.Value)), "all local rules use absolute paths");
Check(rules.All(rule => !RulePortability.IsPortable(rule)), "machine-specific executable rules retain local-only status");
Check(catalog.FromExecutable(nonExecutable) is null, "manual EXE selection rejects DLL files");
Check(catalog.FromExecutable(Path.Combine(root, "missing.exe")) is null, "missing executable is not presented as installed");
Check(catalog.ScanDirectory(Path.Combine(root, "uninstalled-app")) is null, "missing install location is excluded");
Check(catalog.FromExecutable(@"Z:\nonexistent-fixture-" + Guid.NewGuid().ToString("N") + @"\missing.exe") is null, "nonexistent other-drive entry is excluded");
Check(catalog.FromExecutable("alpha.exe") is null, "relative executable paths are rejected");
Check(catalog.FromExecutable(@"\\server\share\alpha.exe") is null, "network paths are not probed by local installation discovery");
Check(catalog.ScanDirectory(Path.GetPathRoot(root)!) is null, "drive roots cannot be scanned as one application");
Directory.CreateDirectory(Path.Combine(root, "Program Files"));
Check(catalog.ScanDirectory(Path.Combine(root, "Program Files")) is null, "generic program container cannot be scanned as one application");
var manual = catalog.FromExecutable(alpha)!;
Check(manual.Executables.Count == 1 && manual.Executables[0].Path == alpha, "manual entry preserves selected executable only");
Check(Path.GetPathRoot(manual.Executables[0].Path) == Path.GetPathRoot(root), "existing paths retain their actual drive rather than assuming C");

var clash = Entry(@"ClashFixture\clash-verge.exe");
Entry(@"ClashFixture\resources\helper.exe");
var known = catalog.ScanDirectory(Path.GetDirectoryName(clash)!)!;
Check(known.Executables.All(entry => entry.ApplicationKey == "known:clash"), "known main executable maps its verified installation components consistently");
var knownRules = InstalledApplicationCatalog.GetAppRules(known);
Check(knownRules.Count == 1 && knownRules[0].Kind == RuleKind.Application && knownRules[0].Value == "known:clash", "known app maps to one portable application rule");
var steam = Entry(@"SteamFixture\steam.exe");
Entry(@"SteamFixture\bin\steamwebhelper.exe");
Entry(@"SteamFixture\unrelated-tool.exe");
Entry(@"SteamFixture\steamapps\game.exe");
var steamScan = catalog.ScanDirectory(Path.GetDirectoryName(steam)!)!;
Check(steamScan.Executables.Count == 2 && steamScan.Executables.All(entry => entry.ApplicationKey == "known:steam"), "Steam installation includes only recognized client components");
var nodeRoot = Entry(@"NodeFixture\main.exe");
Entry(@"NodeFixture\node_modules\unrelated.exe");
Check(catalog.ScanDirectory(Path.GetDirectoryName(nodeRoot)!)!.Executables.Count == 1, "dependency directories are not recursively added as applications");
var package = new InstalledApplication { Name = "Package fixture", ApplicationKey = "package:Contoso.Calculator_8wekyb3d8bbwe" };
var packageRules = InstalledApplicationCatalog.GetAppRules(package);
Check(packageRules.Count == 1 && RulePortability.IsPortable(packageRules[0]), "installed package identity can be kept even when its executable is unreadable");
Check(InstalledApplicationCatalog.GetAppRules(new InstalledApplication { Name = "Unknown", ApplicationKey = "path:C:\\missing.exe" }).Count == 0,
    "an app-level local identity is not silently broadened without a valid executable");
var vanished = new InstalledApplication { Name = "Vanished", Executables = [new InstalledExecutable { Path = Path.Combine(root, "deleted.exe"), Name = "deleted.exe", ApplicationKey = "path:deleted" }] };
Check(InstalledApplicationCatalog.GetAppRules(vanished).Count == 0, "a removed local executable is not converted to a rule");
var bounded = Path.Combine(root, "Bounded");
for (var i = 0; i < InstalledApplicationCatalog.MaximumExecutablesPerDirectory + 20; i++) Entry(@"Bounded\component-" + i + ".exe");
Check(catalog.ScanDirectory(bounded)!.Executables.Count == InstalledApplicationCatalog.MaximumExecutablesPerDirectory, "per-directory executable discovery is bounded");
Check(catalog.Warnings.Any(warning => warning.Contains("截断")), "bounded discovery reports truncation");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    void Canceled(Action action, string label)
    {
        var refused = false;
        try { action(); } catch (OperationCanceledException) { refused = true; }
        Check(refused, label);
    }
    Canceled(() => catalog.Scan(canceled.Token), "canceled preload exits before enumerating installed packages or registry");
    Canceled(() => catalog.ScanDirectory(bounded, canceled.Token), "manual directory scan honors cancellation before filesystem work");
    Canceled(() => catalog.FromExecutable(alpha, canceled.Token), "manual executable metadata lookup honors cancellation");
}

var isolated = Entry(@"Isolated\external.exe");
var linkedRoot = Path.Combine(root, "LinkedFixture"); Directory.CreateDirectory(linkedRoot); Entry(@"LinkedFixture\local.exe");
try
{
    var link = Path.Combine(linkedRoot, "linked"); Directory.CreateSymbolicLink(link, Path.GetDirectoryName(isolated)!);
    Check(catalog.ScanDirectory(linkedRoot)!.Executables.Count == 1, "directory scan does not traverse reparse points");
    Check(catalog.ScanDirectory(link) is null, "a selected link directory is rejected");
    Check(catalog.FromExecutable(Path.Combine(link, "external.exe")) is null, "executables reached through a linked ancestor are rejected");
}
catch (UnauthorizedAccessException) { skipped++; Report("SKIP: symbolic-link fixture requires link creation privileges; no product action was attempted."); }

if (args.Contains("--live-catalog", StringComparer.Ordinal))
{
    var clock = Stopwatch.StartNew(); var installed = catalog.Scan(); clock.Stop();
    Check(installed.Count > 0, "read-only live scan finds installed applications");
    Check(installed.Select(app => app.Id).Distinct(StringComparer.Ordinal).Count() == installed.Count, "live catalog IDs are unique");
    Check(installed.All(app => app.Executables.All(entry => Path.IsPathFullyQualified(entry.Path))), "live executable candidates all have absolute paths");
    Check(installed.Any(app => app.ApplicationKey.StartsWith("package:", StringComparison.Ordinal) || app.IdentityEvidence.Contains("Windows 包")), "live scan includes current-user Windows packages");
    Report($"LIVE: {installed.Count} installations; {installed.Sum(app => app.Executables.Count)} executable candidates; {catalog.Warnings.Count} warnings; {clock.Elapsed.TotalSeconds:F2} seconds.");
    Report("LIVE drives: " + string.Join(", ", installed.SelectMany(app => app.Executables).Select(entry => Path.GetPathRoot(entry.Path)).Distinct(StringComparer.OrdinalIgnoreCase)));
}
Report($"PASS: {assertions} installed-catalog assertions; {skipped} fixture scenarios skipped. No application was started, closed, installed or uninstalled.");
