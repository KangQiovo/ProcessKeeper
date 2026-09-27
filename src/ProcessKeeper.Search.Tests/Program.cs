using System.Globalization;
using ProcessKeeper.Core;

var passed = 0;
var failed = 0;
void Check(bool condition, string message)
{
    Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
    if (condition) passed++; else failed++;
}
void RuleIds(RuleSearchIndex index, IReadOnlyList<WhitelistRule> rules, string query,
    string message, params string[] expected)
{
    var actual = index.MatchingRuleIds(rules, query);
    Check(actual.SetEquals(expected), message + $" | actual={string.Join(',', actual.Order())}");
}
WhitelistRule Rule(string id, RuleKind kind, string value, bool enabled = true) =>
    new() { Id = id, Kind = kind, Value = value, Name = "Rule " + id, Enabled = enabled };

var worker = new ProcessRecord
{
    Id = 8642, Name = "render-worker.exe", Path = @"Q:\Search Fixtures\Orion\render-worker.exe",
    Description = "Compositor component", ProductName = "Prism Engine", Company = "Northwind Labs",
    ApplicationName = "Orion Studio", ApplicationKey = "known:orion", SessionId = 3,
};
foreach (var (query, field) in new[]
{
    ("render-worker", "process filename"), (@"Fixtures\Orion", "full path"),
    ("compositor", "description"), ("prism", "product"), ("studio", "owning application name"),
    ("northwind", "company"), ("8642", "exact PID"), ("RENDER-WORKER.EXE", "case-insensitive filename"),
    ("", "empty query")
}) Check(ApplicationSearch.MatchesProcess(worker, query), "process lookup matches " + field);
foreach (var query in new[] { "864", "08642", "+8642", "86420", "unrelated" })
    Check(!ApplicationSearch.MatchesProcess(worker, query), "process lookup rejects unrelated value or inexact PID " + query);
Check(!ApplicationSearch.MatchesProcess(new ProcessRecord { Id = 7 }, "worker"), "missing process metadata does not invent a match");
Check(ApplicationSearch.Contains(null, "") && !ApplicationSearch.Contains(null, "name"), "empty and missing fields are handled consistently");
var savedCulture = CultureInfo.CurrentCulture;
try
{
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
    Check(ApplicationSearch.MatchesProcess(worker, "PRISM"), "matching is ordinal and does not change under Turkish culture");
    Check(ApplicationSearch.MatchesProcess(worker, "8642"), "PID matching remains invariant across cultures");
}
finally { CultureInfo.CurrentCulture = savedCulture; }

var main = worker with { Id = 1100, Name = "orion.exe", Path = @"Q:\Search Fixtures\Orion\orion.exe", Description = "Editor" };
var owner = new ApplicationGroup
{
    Key = "known:orion", Name = "Orion Studio", Company = "Northwind Labs", Description = "Design suite", Processes = [main, worker]
};
var other = new ApplicationGroup { Key = "known:other", Name = "Other Studio", Processes = [new ProcessRecord { Id = 15, Name = "other.exe" }] };
Check(!ApplicationSearch.MatchesApplicationMetadata(owner, "render-worker") && ApplicationSearch.MatchesApplication(owner, "render-worker"),
    "a child-only process search finds its owning application even when owner metadata differs");
var appResults = new[] { owner, other }.Where(app => ApplicationSearch.MatchesApplication(app, "render-worker")).ToArray();
Check(appResults.Length == 1 && ReferenceEquals(appResults[0], owner) && appResults[0].Processes.Count == 2,
    "filtering by a child retains the original owner and its complete process relationship");
Check(ApplicationSearch.MatchesApplication(owner, "design suite"), "running application description is searchable");
Check(new[] { owner, other }.All(app => ApplicationSearch.MatchesApplication(app, "")), "empty running search preserves all owners");

var system = worker with { Id = 9876, Name = "secret-kernel-worker.exe", Description = "Restricted kernel helper", IsSystem = true, SessionId = 0 };
var snapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, [main, worker, system], [owner]);
var disabled = Rule("disabled-owner", RuleKind.Application, "known:orion", false);
var allMatches = new RuleProcessMatcher(snapshot).Match(disabled);
var visibleMatches = allMatches.Where(match => !match.Process.IsSystem).ToArray();
Check(allMatches.Count == 3 && visibleMatches.Length == 2, "disabled rule preview can describe current processes without granting protection");
Check(ApplicationSearch.MatchesRule(disabled, "My editor", visibleMatches, "render-worker"), "disabled whitelist entry is discoverable by a visible matched process");
Check(!ApplicationSearch.MatchesRule(disabled, "My editor", visibleMatches, "secret-kernel"), "whitelist search cannot reveal excluded system process metadata");
Check(!ApplicationSearch.MatchesRule(disabled, "My editor", visibleMatches, "9876"), "whitelist search cannot reveal an excluded system PID");
Check(ApplicationSearch.MatchesRule(disabled, "My editor", allMatches, "secret-kernel"), "system-inclusive whitelist preview can search explicitly included system processes");
Check(ApplicationSearch.MatchesRule(disabled, "My editor", [], "my EDITOR"), "whitelist display name remains searchable without running processes");
Check(ApplicationSearch.MatchesRule(disabled, "My editor", [], "Rule disabled"), "stored rule name remains searchable");
Check(ApplicationSearch.MatchesRule(disabled, "My editor", [], "known:ORION"), "stored rule value remains searchable");
Check(!disabled.Enabled && ReferenceEquals(allMatches.First(match => match.Process.Id == worker.Id).Process, worker),
    "search preserves disabled state and process identity rather than modifying protection");

var alphaMain = new InstalledExecutable { Name = "alpha.exe", Path = @"Q:\Tools\Alpha\alpha.exe", Description = "Editor bootstrap", ApplicationKey = "known:alpha" };
var alphaWorker = new InstalledExecutable { Name = "worker.exe", Path = @"Q:\Tools\Alpha\components\worker.exe", Description = "Aurora renderer", ApplicationKey = "known:alpha" };
var betaWorker = new InstalledExecutable { Name = "worker.exe", Path = @"R:\Tools\Beta\worker.exe", Description = "Borealis renderer", ApplicationKey = "known:beta" };
var alpha = new InstalledApplication { Id = "alpha", Name = "Alpha Editor", Publisher = "Alpine Works", InstallLocation = @"Q:\Tools\Alpha", ApplicationKey = "known:alpha", Executables = [alphaMain, alphaWorker] };
var beta = new InstalledApplication { Id = "beta", Name = "Beta Editor", Publisher = "Boreal Works", InstallLocation = @"R:\Tools\Beta", ApplicationKey = "known:beta", Executables = [betaWorker] };
var index = new RuleSearchIndex([alpha, beta]);
var rules = new[]
{
    Rule("alpha-app", RuleKind.Application, "KNOWN:ALPHA"), Rule("beta-app", RuleKind.Application, "known:beta"),
    Rule("alpha-main-path", RuleKind.ExecutablePath, alphaMain.Path), Rule("alpha-worker-path", RuleKind.ExecutablePath, alphaWorker.Path.ToUpperInvariant()),
    Rule("beta-worker-path", RuleKind.ExecutablePath, betaWorker.Path), Rule("alpha-worker-normalized", RuleKind.ExecutablePath, @"Q:\Tools\Alpha\components\..\components\worker.exe"),
    Rule("worker-name", RuleKind.ProcessName, "WORKER.EXE"), Rule("main-name", RuleKind.ProcessName, "alpha.exe"),
    Rule("alpha-directory", RuleKind.Directory, @"Q:\Tools\Alpha"), Rule("alpha-component-directory", RuleKind.Directory, @"q:/tools/alpha/components/"),
    Rule("beta-directory", RuleKind.Directory, @"R:\Tools\Beta"), Rule("prefix-collision", RuleKind.Directory, @"Q:\Tools\Al"),
    Rule("file-is-not-directory", RuleKind.Directory, alphaWorker.Path), Rule("absent-app", RuleKind.Application, "known:absent"),
    Rule("invalid-path", RuleKind.ExecutablePath, "worker.exe"), Rule("invalid-directory", RuleKind.Directory, @"Q:\Tools\*"),
    Rule("disabled-alpha-app", RuleKind.Application, "known:alpha", false)
};
RuleIds(index, rules, "Aurora", "offline component description maps all eligible rule kinds without associating another same-named executable",
    "alpha-app", "alpha-worker-path", "alpha-worker-normalized", "worker-name", "alpha-directory", "alpha-component-directory", "disabled-alpha-app");
RuleIds(index, rules, "Alpha Editor", "offline owning application name maps the app and all its known components",
    "alpha-app", "alpha-main-path", "alpha-worker-path", "alpha-worker-normalized", "worker-name", "main-name", "alpha-directory", "alpha-component-directory", "disabled-alpha-app");
RuleIds(index, rules, "Alpine", "offline publisher search preserves owning application association",
    "alpha-app", "alpha-main-path", "alpha-worker-path", "alpha-worker-normalized", "worker-name", "main-name", "alpha-directory", "alpha-component-directory", "disabled-alpha-app");
RuleIds(index, rules, @"Q:\Tools\Alpha", "offline install-location search is path-specific despite duplicate basenames",
    "alpha-app", "alpha-main-path", "alpha-worker-path", "alpha-worker-normalized", "worker-name", "main-name", "alpha-directory", "alpha-component-directory", "disabled-alpha-app");
RuleIds(index, rules, "worker.exe", "shared executable name finds both verified owning installations",
    "alpha-app", "beta-app", "alpha-worker-path", "alpha-worker-normalized", "beta-worker-path", "worker-name", "alpha-directory", "alpha-component-directory", "beta-directory", "disabled-alpha-app");
RuleIds(index, rules, "BOREALIS", "offline component matching ignores case while preserving exact path ownership", "beta-app", "beta-worker-path", "worker-name", "beta-directory");
RuleIds(index, rules, "absent query", "missing offline software does not invent rule associations");
RuleIds(index, rules, "", "empty offline search retains only rules associated with installed metadata",
    "alpha-app", "beta-app", "alpha-main-path", "alpha-worker-path", "alpha-worker-normalized", "beta-worker-path", "worker-name", "main-name", "alpha-directory", "alpha-component-directory", "beta-directory", "disabled-alpha-app");
Check(rules.Single(rule => rule.Id == "disabled-alpha-app").Enabled == false && alpha.Executables.Count == 2,
    "offline search changes neither disabled protection state nor installation metadata");

var standalone = new InstalledApplication { Id = "metadata-only", Name = "Metadata-only application", ApplicationKey = "known:metadata", Executables = [] };
RuleIds(new RuleSearchIndex([standalone]), [Rule("metadata-rule", RuleKind.Application, "known:metadata")], "Metadata-only", "app-level installation metadata is searchable even without executable candidates", "metadata-rule");
var splitComponent = alphaWorker with { ApplicationKey = "path:q:/tools/alpha/components/worker.exe" };
var splitOwner = alpha with { Executables = [splitComponent] };
RuleIds(new RuleSearchIndex([splitOwner]),
    [Rule("owner-key", RuleKind.Application, alpha.ApplicationKey), Rule("component-key", RuleKind.Application, splitComponent.ApplicationKey)],
    "Aurora", "a matched offline component retains its owner application key as well as its own component key", "owner-key", "component-key");
var unkeyedOwner = alpha with { Executables = [alphaWorker with { ApplicationKey = "" }] };
RuleIds(new RuleSearchIndex([unkeyedOwner]), [Rule("owner-key", RuleKind.Application, alpha.ApplicationKey)], "Aurora",
    "an offline component without a key remains discoverable through its explicit owning application", "owner-key");

var bulkApps = Enumerable.Range(0, 2400).Select(number => new InstalledApplication
{
    Id = $"bulk-{number}", Name = $"Bulk application {number}", ApplicationKey = $"bulk:{number}",
    Executables = [new InstalledExecutable
    {
        Name = "worker.exe", Path = $@"Q:\BulkSearchFixture\App{number}\worker.exe",
        Description = $"Exact component [{number}]", ApplicationKey = $"bulk:{number}"
    }]
}).ToArray();
var bulkRules = bulkApps.SelectMany(app => new[]
{
    Rule(app.Id + "-app", RuleKind.Application, app.ApplicationKey),
    Rule(app.Id + "-path", RuleKind.ExecutablePath, app.Executables[0].Path),
    Rule(app.Id + "-directory", RuleKind.Directory, Path.GetDirectoryName(app.Executables[0].Path)!)
}).ToArray();
var bulkIndex = new RuleSearchIndex(bulkApps);
RuleIds(bulkIndex, bulkRules, "[2399]", "large offline catalog keeps a specific component bound to only its three owning rule entries",
    "bulk-2399-app", "bulk-2399-path", "bulk-2399-directory");
var concurrentResults = await Task.WhenAll(Enumerable.Range(0, 8).Select(number => Task.Run(() =>
    bulkIndex.MatchingRuleIds(bulkRules, $"[{number}]").SetEquals(new[] { $"bulk-{number}-app", $"bulk-{number}-path", $"bulk-{number}-directory" }))));
Check(concurrentResults.All(result => result), "overlapping background search requests cannot mix matches from different queries");
Check(bulkIndex.MatchingRuleIds(bulkRules, "").Count == bulkRules.Length,
    "clearing search after overlapping requests restores all associated rule entries");

Console.WriteLine($"Search: {passed} passed, {failed} failed. Synthetic metadata only; no scans, process operations, settings writes or native windows.");
Environment.ExitCode = failed == 0 ? 0 : 1;
