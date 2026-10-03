using ProcessKeeper.Core;
namespace ProcessKeeper.App;
internal static class BulkSelectionVerification
{
    internal static void VerifyRevisionAndCulture(Action<bool, string> check)
    {
        var previous = global::System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            global::System.Globalization.CultureInfo.CurrentCulture = global::System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            var display = new ApplicationDisplayCatalog(new[] { new GameCatalogEntry("test", "INDIGO", @"Q:\Demo\Alpha", GamePlatformCatalog.Steam) }, Array.Empty<GamePlatformClient>(), Array.Empty<string>());
            var result = BulkSelectionPlan.Create(0, true, "\u0131", 0, "", false, false, false, display, Snapshot, Installed, Initial, CancellationToken.None);
            check(result.Changed == 0, "batch platform matching uses the same ordinal search as visible results under Turkish culture");
        }
        finally { global::System.Globalization.CultureInfo.CurrentCulture = previous; }
        var directory = Path.Combine(Path.GetTempPath(), "ProcessKeeper-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            new WhitelistStore(directory).Save(Initial);
            var store = new WhitelistProfilesStore(directory); var captured = store.Load();
            var planned = BulkSelectionPlan.Create(0, true, "Alpha", 0, "", false, false, false, Display, Snapshot, Installed, captured.ActiveRules, CancellationToken.None);
            var other = store.Create("Other profile", captured.Revision);
            other = store.Save(other.Profiles.Single(p => p.Id != other.ActiveId).Id, Initial, other.Revision);
            captured = other;
            var switched = store.Activate(other.Profiles.Single(p => p.Id != other.ActiveId).Id, other.Revision);
            var bytes = File.ReadAllBytes(store.FilePath); bool rejected = false;
            try { store.Save(captured.ActiveId, planned.Rules, captured.Revision); } catch(InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "batch CAS rejects switching to another profile even when active rules were identical");
            captured = store.Load();
            new WhitelistStore(directory).Save(new[] { Rule("outside", RuleKind.Application, "known:qq") }); bytes = File.ReadAllBytes(store.FilePath); rejected = false;
            try { store.Save(captured.ActiveId, planned.Rules, captured.Revision); } catch(InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllBytes(store.FilePath).SequenceEqual(bytes), "batch CAS preserves edits arriving between planning and atomic commit");
            captured = store.Load(); var inactive = captured.Profiles.Single(p => p.Id != captured.ActiveId);
            var saved = store.Save(captured.ActiveId, planned.Rules, captured.Revision);
            check(saved.Profiles.Single(p => p.Id == inactive.Id).Rules.SequenceEqual(inactive.Rules) && saved.ActiveRules.SequenceEqual(planned.Rules), "batch saves only active rules and preserves inactive profiles");
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static readonly ProcessRecord Alpha = new() { Id = 7001, Name = "alpha.exe", Path = @"Q:\Demo\Alpha\alpha.exe", ApplicationKey = "package:Fixture.Alpha_123456789abcd", ApplicationName = "Alpha Editor", SessionId = 1, OwnerSid = "fixture", StartTimeUtcTicks = 100, MemoryBytes = 1024 };
    internal static readonly ProcessRecord Beta = Alpha with { Id = 7002, Name = "beta.exe", Path = @"R:\Demo\Beta\beta.exe", ApplicationKey = "package:Fixture.Beta_123456789abcd", ApplicationName = "Beta Editor" };
    internal static readonly ProcessRecord System = Alpha with { Id = 7003, Name = "winlogon.exe", Path = @"Q:\System\winlogon.exe", ApplicationKey = "known:system", ApplicationName = "System", IsSystem = true };
    internal static readonly ProcessRecord Unknown = Alpha with { Id = 7004, Name = "unknown.exe", ApplicationKey = "unknown:7004", ApplicationName = "Unknown" };
    internal static readonly ProcessRecord Microsoft = Beta with { Id = 7005, Name = "official.exe", Path = @"R:\Demo\Official\official.exe", ApplicationKey = "package:Fixture.Official_123456789abcd", ApplicationName = "Official" };
    internal static readonly ProcessSnapshot Snapshot = new(DateTimeOffset.Now, new[] { Alpha, Beta, System, Unknown, Microsoft }, new[] { Alpha, Beta, System, Unknown, Microsoft }.Select(p => new ApplicationGroup { Key = p.ApplicationKey, Name = p.ApplicationName, Processes = new[] { p } }).ToArray());
    internal static readonly InstalledApplication[] Installed = new[] { Alpha, Beta, Microsoft }.Select(p => new InstalledApplication { Id = p.ApplicationKey, Name = p.ApplicationName, ApplicationKey = p.ApplicationKey, InstallLocation = Path.GetDirectoryName(p.Path)!, Executables = new[] { new InstalledExecutable { Name = p.Name, Path = p.Path, ApplicationKey = p.ApplicationKey } } }).ToArray();
    internal static readonly ApplicationDisplayCatalog Display = new(new[] { new GameCatalogEntry("alpha", "Manifest Alpha Game", @"Q:\Demo\Alpha", GamePlatformCatalog.Steam), new GameCatalogEntry("beta", "Beta Game", @"R:\Demo\Beta", GamePlatformCatalog.Steam) }, Array.Empty<GamePlatformClient>(), new[] { Microsoft.Path });
    internal static WhitelistRule Rule(string id, RuleKind kind, string value, bool enabled = true) => new() { Id = id, Name = id, Kind = kind, Value = value, Enabled = enabled };
    internal static IReadOnlyList<WhitelistRule> Initial => new[] { Rule("a", RuleKind.Application, Alpha.ApplicationKey, false), Rule("b", RuleKind.Application, Beta.ApplicationKey), Rule("broad", RuleKind.Directory, @"Q:\Demo"), Rule("name", RuleKind.ProcessName, "alpha.exe"), Rule("system", RuleKind.Application, System.ApplicationKey) };
    internal static void Verify(Action<bool, string> check, bool legacy)
    {
        BulkSelectionPlan.Result Plan(int page, bool enabled, string query = "", string drive = "", bool systems = false, bool hide = false, IReadOnlyList<WhitelistRule>? rules = null, int category = 0) => BulkSelectionPlan.Create(page, enabled, query, category, drive, systems, legacy, hide, Display, Snapshot, Installed, rules ?? Initial, CancellationToken.None);
        var result = Plan(0, true, "alpha.exe"); check(result.Changed == 1 && result.Rules.Single(r => r.Id == "a").Enabled && result.Rules.Count == Initial.Count, "process search enables owner rule and preserves ID without duplicates");
        result = Plan(0, false, "Alpha", rules: result.Rules); check(result.Changed == 1 && !result.Rules.Single(r => r.Id == "a").Enabled && result.Rules.Single(r => r.Id == "b").Enabled && result.Rules.Single(r => r.Id == "broad").Enabled && result.Rules.Single(r => r.Id == "name").Enabled, "deselect preserves filtered-out and broad/name protections");
        result = Plan(0, true, "Steam", rules: Array.Empty<WhitelistRule>()); check(result.Rules.Count == 2 && result.Rules.All(r => r.Value == Alpha.ApplicationKey || r.Value == Beta.ApplicationKey), "collapsed game group metadata scope selects both independent games, never header");
        result = Plan(0, true, hide: true, rules: Array.Empty<WhitelistRule>()); check(result.Rules.Count == 2 && !result.Rules.Any(r => r.Value == Microsoft.ApplicationKey || r.Value == Unknown.ApplicationKey || r.Value == System.ApplicationKey), "default system and verified Microsoft filters plus unknown identity guard respected");
        result = Plan(0, true, "winlogon", systems: true, rules: Array.Empty<WhitelistRule>()); check(result.Rules.Count == 1 && new ProtectionPolicy(1,"fixture",99999).Evaluate(System,Snapshot,result.Rules).Protected, "explicit system selection cannot alter mandatory close protection");
        result = Plan(0, true, "", category: 1, rules: Array.Empty<WhitelistRule>()); check(result.Changed == 0, "running category filter limits batch scope");
        result = Plan(1, true, "alpha.exe", rules: Array.Empty<WhitelistRule>()); check(result.Rules.Count == 1 && result.Rules[0].Value == Alpha.ApplicationKey, "installed process search chooses only owner software identity");
        result = Plan(1, true, drive: "R:", hide: true, rules: Array.Empty<WhitelistRule>()); check(result.Rules.Count == 1 && result.Rules[0].Value == Beta.ApplicationKey, "installed drive and verified publisher filters combined");
        result = Plan(1, false, "Beta"); check(!result.Rules.Single(r => r.Id == "b").Enabled && result.Rules.Single(r => r.Id == "system").Enabled, "installed deselect only exact matched identity");
        result = Plan(2, false, "alpha.exe", rules: Plan(0, true, "Alpha").Rules); check(!result.Rules.Single(r => r.Id == "a").Enabled && result.Rules.Single(r => r.Id == "b").Enabled, "whitelist process search toggles matching rules without touching unrelated IDs");
        result = Plan(2, false, "winlogon"); check(result.Changed == 0 && result.Rules.Single(r => r.Id == "system").Enabled, "hidden system-only query cannot bulk toggle its rule");
        result = Plan(2, false, "winlogon", systems: true); check(result.Changed == 1 && !result.Rules.Single(r => r.Id == "system").Enabled, "whitelist explicit system search toggles only rule, not policy");
        result = Plan(2, false, "no-match"); check(result.Changed == 0 && result.Rules.SequenceEqual(Initial), "empty results remain no-op");
        result = Plan(0, true, "Alpha", rules: Plan(0, true, "Alpha").Rules); check(result.Changed == 0, "repeat select is idempotent");
        var cancelled = new CancellationToken(true); bool rejected = false; try { BulkSelectionPlan.Create(0, true, "", 0, "", false, legacy, false, Display, Snapshot, Installed, Initial, cancelled); } catch (OperationCanceledException) { rejected = true; } check(rejected, "planning honors cancellation");
        var full = Enumerable.Range(0,1000).Select(i => Rule("full"+i, RuleKind.Application, "known:full"+i)).ToArray(); rejected=false;try { Plan(0,true,"Alpha",rules:full); } catch(InvalidDataException) {rejected=true;} check(rejected, "over-capacity batch rejects atomically rather than silently partially selecting");
    }
}
