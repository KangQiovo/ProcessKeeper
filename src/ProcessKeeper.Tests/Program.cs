using System.Diagnostics;
using System.Security.Principal;
using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

if (args.Contains("--owned-test-child"))
{
    Console.WriteLine("OWNED_TEST_CHILD_READY");
    await Task.Delay(Timeout.Infinite);
    return;
}

int assertions = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + label);
    assertions++;
    Console.WriteLine("PASS: " + label);
}

const string Sid = "S-1-5-21-100-200-300-1001";
long epoch = DateTime.UtcNow.AddHours(-1).Ticks;
ProcessRecord P(int id, string key = "unknown", int parent = 0) => new()
{
    Id = id, ParentId = parent, SessionId = 1, OwnerSid = Sid, Name = "sample.exe",
    Path = @"C:\Apps\Sample\sample.exe", StartTimeUtcTicks = epoch + id * 100, ApplicationKey = key
};
ProcessSnapshot Snapshot(params ProcessRecord[] processes) => new(DateTimeOffset.UtcNow, processes, []);
WhitelistRule Rule(RuleKind kind, string value, bool descendants = false) => new()
{ Name = "测试", Kind = kind, Value = value, IncludeDescendants = descendants };
var policy = new ProtectionPolicy(1, Sid, 999);
SensitiveProcessCloseVerification.Run(Check);
RiskConfirmationModeVerification.Run(Check);
ReleaseBoundaryVerification.Run(Check);
WhitelistProfilesVerification.Run(Check);
Check(WhitelistStore.CreateDefaults().Count == 0, "every release channel starts with an empty personal whitelist");
var defaults = HistoricalWhitelistFixture.Create();
bool Protected(ProcessRecord process, IReadOnlyList<WhitelistRule>? rules = null, params ProcessRecord[] others) =>
    policy.Evaluate(process, Snapshot([process, .. others]), rules ?? defaults).Protected;

Check(defaults.Count == 7, "historical seven-rule fixture remains migratable user data");
foreach (var key in new[] { "clash", "codex", "qq", "steam", "huorong", "uu", "translucenttb" })
    Check(Protected(P(100, "known:" + key)), "default application " + key);
Check(defaults.Single(r => r.Value == "known:codex").IncludeDescendants, "Codex keeps tool descendants");
Check(defaults.Where(r => r.Value != "known:codex").All(r => !r.IncludeDescendants), "other defaults do not inherit");
Check(!Protected(P(100)), "ordinary application is closable");
Check(Protected(P(999)), "self PID is always protected");
Check(Protected(P(100) with { IsSelf = true }), "self marker is protected");
Check(Protected(P(100) with { IsSystem = true }), "system marker is protected");
Check(Protected(P(100) with { Name = "LsAsS.ExE" }), "critical image name is protected case insensitively");
Check(Protected(P(100) with { SessionId = 0 }), "session zero is protected");
Check(Protected(P(100) with { SessionId = 2 }), "other session is protected");
Check(Protected(P(100) with { OwnerSid = "S-1-5-18" }), "SYSTEM account is protected");
Check(Protected(P(100) with { OwnerSid = "" }), "unknown owner is protected");
Check(new ProtectionPolicy(1, "", 999).Evaluate(P(100), Snapshot(P(100)), []).Protected, "unknown current owner protects everything");
Check(Protected(P(100) with { Path = "" }), "unknown path is protected");
Check(Protected(P(100) with { Path = @"Apps\sample.exe" }), "relative path is protected");
Check(Protected(P(100) with { StartTimeUtcTicks = 0 }), "unknown creation time is protected");
Check(Protected(P(100) with { StartTimeUtcTicks = DateTime.MaxValue.Ticks }), "future creation time is protected");
Check(!Protected(P(100), [Rule(RuleKind.ProcessName, "sam")]), "name rule is exact");
Check(Protected(P(100), [Rule(RuleKind.ProcessName, "SAMPLE.EXE")]), "name rule ignores case");
Check(!Protected(P(100), [Rule(RuleKind.ProcessName, "sample.exe") with { Enabled = false }]), "disabled rule has no effect");
Check(Protected(P(100), [Rule(RuleKind.ExecutablePath, @"c:\APPS\sample\SAMPLE.exe")]), "exact executable path ignores case");
Check(!Protected(P(100), [Rule(RuleKind.ExecutablePath, @"C:\Other\sample.exe")]), "same name in different directory is not matched");
Check(Protected(P(100), [Rule(RuleKind.Directory, @"C:\Apps\Sample")]), "directory rule includes contained image");
Check(Protected(P(100), [Rule(RuleKind.Directory, @"C:\Apps\Sample\")]), "directory trailing slash accepted");
Check(!Protected(P(100), [Rule(RuleKind.Directory, @"C:\Apps\Sam")]), "directory prefix has boundary");
Check(!Protected(P(100) with { Path = @"C:\Apps\SampleCopy\sample.exe" }, [Rule(RuleKind.Directory, @"C:\Apps\Sample")]), "sibling directory not matched");
Check(Protected(P(100), [Rule(RuleKind.Directory, @"C:\")]), "root directory boundary works");
Check(Protected(P(100, "KNOWN:QQ")), "application rule ignores case");

var codex = P(20, "known:codex");
var helper = P(30, parent: 20);
var grandchild = P(40, parent: 30);
Check(Protected(helper, null, codex), "Codex direct tool child retained");
Check(Protected(grandchild, null, codex, helper), "Codex tool grandchild retained");
Check(!Protected(helper, null, codex with { StartTimeUtcTicks = helper.StartTimeUtcTicks + 1 }), "reused parent PID does not inherit");
Check(!Protected(helper, null, codex with { StartTimeUtcTicks = 0 }), "unknown parent creation time does not inherit");
Check(!Protected(helper, null, codex with { OwnerSid = "S-1-5-18" }), "cross owner parent does not inherit");
Check(!Protected(helper, null, codex with { SessionId = 2 }), "cross session parent does not inherit");
Check(!Protected(helper, null, codex with { Path = "" }), "unknown parent path does not inherit");
Check(!Protected(helper, null, codex, codex), "ambiguous duplicate parent PID does not inherit");
Check(!Protected(helper, null), "missing parent does not inherit");
Check(!Protected(helper with { ParentId = 30 }), "parent cycle does not inherit");
Check(!Protected(P(40, parent: 20), null, P(20, "known:steam")), "Steam launched games remain closable");
Check(!Protected(P(40, "known:mydock", 20), null, P(20, "known:steam")), "Steam launched MyDock remains closable");
Check(!Protected(helper with { ApplicationKey = "known:avd" }, null, codex), "Codex AVD exception");
Check(!Protected(helper with { ApplicationKey = "known:mydock" }, null, codex), "Codex MyDock exception");
Check(!Protected(helper with { Name = "qemu-system-x86_64.exe" }, null, codex), "Codex qemu exception by process name");
Check(!Protected(grandchild, null, codex, helper with { ApplicationKey = "known:avd" }), "Codex AVD descendants not inherited");
Check(Protected(helper with { ApplicationKey = "known:avd" }, [.. defaults, Rule(RuleKind.Application, "known:avd")], codex), "user explicit AVD rule still works");
var customCodex = Rule(RuleKind.Application, "known:codex", true);
Check(Protected(helper with { ApplicationKey = "known:avd" }, [customCodex], codex), "custom descendant rule is explicit user choice");

var dock = P(100, "known:mydock") with { Name = "Dock_64.exe", Path = @"C:\Apps\MyDockFinder\Dock_64.exe", OwnerSid = "S-1-5-18" };
Check(!Protected(dock), "verified SYSTEM Dock component in interactive session is closable");
Check(Protected(dock with { SessionId = 0 }), "SYSTEM Dock session zero remains protected");
Check(Protected(dock with { Path = @"C:\Apps\MyDockFinderCopy\Dock_64.exe" }), "Dock exception has directory boundary");
Check(Protected(dock with { Path = @"C:\Apps\MyDockFinder\other.exe" }), "Dock exception verifies path filename");
Check(Protected(dock with { OwnerSid = "S-1-5-21-400-500-600-1001" }), "Dock exception does not cross other users");
Check(Protected(dock with { Name = "MyDock.exe", Path = @"C:\Apps\MyDockFinder\MyDock.exe" }), "MyDock service executable is not SYSTEM exception");
Check(Protected(dock with { StartTimeUtcTicks = 0 }), "Dock identity still requires creation time");
var serviceDescriptor = new ProcessRecord { Name = "MyDock.exe", Path = @"C:\Apps\MyDockFinder\MyDock.exe", ApplicationKey = "known:mydock" };
Check(ProtectionPolicy.IsDirectlyWhitelisted(serviceDescriptor, [Rule(RuleKind.Directory, @"C:\Apps\MyDockFinder")]), "direct whitelist check protects service descriptor by directory without process identity");
Check(!ProtectionPolicy.IsDirectlyWhitelisted(serviceDescriptor, [Rule(RuleKind.Application, "known:mydock") with { Enabled = false }]), "direct whitelist check excludes disabled service rule");

var examples = new[]
{
    P(101) with { Name="steam.exe", Path=@"D:\steam\steam.exe" },
    P(102, parent:101) with { Name="game.exe", Path=@"D:\steam\steamapps\common\game\game.exe" },
    P(103, parent:101) with { Name="steamwebhelper.exe", Path=@"D:\steam\bin\cef\steamwebhelper.exe" },
    P(104, parent:101) with { Name="MyDock.exe", Path=@"D:\steam\steamapps\common\MyDockFinder\MyDock.exe" },
    P(105) with { Name="codex.exe", Path=@"C:\Users\user\AppData\Local\OpenAI\Codex\bin\hash\codex.exe" },
    P(106, parent:105) with { Name="node.exe", Path=@"C:\Users\user\AppData\Local\OpenAI\Codex\runtimes\node\node.exe" },
    P(107, parent:105) with { Name="python.exe", Path=@"C:\Python\python.exe" },
    P(108, parent:105) with { Name="emulator.exe", Path=@"C:\Android\Sdk\emulator\emulator.exe" },
    P(109, parent:110), P(110), P(111, parent:123456), P(112, parent:112),
    P(113) with { Name="game.exe", Path=@"C:\Other\game.exe" },
    P(114) with { Name="notepad.exe", Path=@"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_1\notepad.exe", PackageFamilyName="Microsoft.WindowsNotepad_8wekyb3d8bbwe" },
    P(115) with { Name="steamwebhelper.exe", Path=@"D:\steam\steamapps\common\game\steamwebhelper.exe" }
};
var classified = ProcessIdentity.ClassifyAll(examples).ToDictionary(p=>p.Id);
Check(classified[101].ApplicationKey == "known:steam", "identity recognizes Steam client");
Check(classified[102].ApplicationKey != "known:steam", "identity separates Steam games");
Check(classified[103].ApplicationKey == "known:steam", "identity recognizes Steam webhelper");
Check(classified[104].ApplicationKey == "known:mydock", "identity separates MyDock from Steam");
Check(classified[106].ApplicationKey == "known:codex", "identity recognizes Codex installed runtime");
Check(classified[107].ApplicationKey != "known:codex", "application grouping does not inherit external Python");
Check(classified[108].ApplicationKey == "known:avd", "identity separates AVD from Codex");
Check(!classified[109].ParentIdentityVerified, "identity detects reused parent PID");
Check(!classified[111].ParentIdentityVerified, "identity rejects missing parent");
Check(!classified[112].ParentIdentityVerified, "identity rejects self parent cycle");
Check(classified[102].ApplicationKey != classified[113].ApplicationKey, "identity separates same filename in different directory");
Check(!classified[114].IsSystem, "packaged ordinary app is not automatically system component");
Check(classified[115].ApplicationKey != "known:steam", "steamapps executable is not Steam helper");
Check(classified[102].ParentIdentityVerified, "identity retains valid startup relationship");
Check((P(100) with { Windows = [new WindowRecord(1,"x",true,false,true,false)] }).Category == RunCategory.Background, "cloaked windows are background");
Check((P(100) with { Windows = [new WindowRecord(1,"x",true,true,false,false)] }).Category == RunCategory.Minimized, "minimized window classification");
Check((P(100) with { SessionId = -1, Services = [new ServiceRecord("Example", "Example service", 100)] }).Category == RunCategory.System, "known service remains service category when session is unreadable");
var absentWindow = new WindowRecord(0, "fixture", true, false, false, false);
var fixtureWindow = absentWindow with { Handle = 1 };
Check(!WindowActions.Activate(P(100), absentWindow).Success, "activate rejects absent window before native operations");
Check(!WindowActions.Minimize(P(100), absentWindow).Success, "minimize rejects absent window before native operations");
Check(!WindowActions.Activate(P(100) with { StartTimeUtcTicks = 0 }, fixtureWindow).Success, "activate requires creation time before native operations");
Check(!WindowActions.Minimize(P(100) with { Path = "" }, fixtureWindow).Success, "minimize requires complete image path before native operations");

Check(defaults.All(RulePortability.IsPortable), "all seven default rules are portable across PCs");
Check(RulePortability.ForSharing(defaults).SequenceEqual(defaults), "sharing keeps all default rules unchanged");
foreach (string knownKey in new[] { "known:avd", "known:mydock", "KNOWN:QQ" })
    Check(RulePortability.IsPortable(Rule(RuleKind.Application, knownKey)), "supported known identity is portable: " + knownKey);
var packageRule = Rule(RuleKind.Application, "package:Microsoft.WindowsNotepad_8wekyb3d8bbwe");
Check(RulePortability.IsPortable(packageRule), "valid stable package family is portable");
Check(RulePortability.IsPortable(packageRule with { Value = packageRule.Value.ToUpperInvariant() }), "package identity portability ignores case");
foreach (string invalidIdentity in new[]
{
    "known:unregistered", @"known:C:\Apps\QQ\qq.exe", @"known:qq\C:\Users\person", "known:qq ",
    @"package:C:\Users\person\app.exe", @"package:Microsoft.App_8wekyb3d8bbwe\helper.exe",
    "package:Microsoft.App_8wekyb3d8bbwe!App", "package:Microsoft.App_1.0.0.0_x64__8wekyb3d8bbwe",
    "package:Microsoft.App_8wekyb3d8bbw", "package:Microsoft.App_8wekyb3d8bbwl", "package:Microsoft.App_8weKyb3d8bbwe", "package:ab_8wekyb3d8bbwe",
    "package:CON_8wekyb3d8bbwe", "package:con.app_8wekyb3d8bbwe", "package:App._8wekyb3d8bbwe",
    "package:xn--App_8wekyb3d8bbwe", "package:App.xn--name_8wekyb3d8bbwe", "package:应用_8wekyb3d8bbwe",
    "package:" + new string('a', 51) + "_8wekyb3d8bbwe", "package:Microsoft.App_8wekyb3d8bbwe\n",
    @"path:C:\Apps\Local\local.exe", "unknown:123:456", ""
})
    Check(!RulePortability.IsPortable(Rule(RuleKind.Application, invalidIdentity)), "nonportable or malformed identity is rejected: " + invalidIdentity.Replace("\n", "\\n"));
var localNameRule = Rule(RuleKind.ProcessName, "app.exe");
var localPathRule = Rule(RuleKind.ExecutablePath, @"C:\Users\alice\App\app.exe");
var localDirectoryRule = Rule(RuleKind.Directory, @"C:\Users\alice\App");
var localApplicationRule = Rule(RuleKind.Application, @"path:C:\Users\alice\App\app.exe");
var unknownApplicationRule = Rule(RuleKind.Application, "unknown:123:456");
foreach (var localRule in new[] { localNameRule, localPathRule, localDirectoryRule, localApplicationRule, unknownApplicationRule })
    Check(!RulePortability.IsPortable(localRule), "machine-specific rule is not portable: " + localRule.Kind + " " + localRule.Value);
var portabilitySource = new[]
{
    defaults[0], defaults[1] with { Enabled = false }, packageRule,
    localNameRule, localPathRule with { Enabled = false }, localDirectoryRule, localApplicationRule, unknownApplicationRule
};
var sourceBefore = portabilitySource.ToArray();
var sharedRules = RulePortability.ForSharing(portabilitySource);
Check(sharedRules.SequenceEqual(portabilitySource.Take(3)), "sharing excludes paths process names and unknown identities");
var preparedRules = RulePortability.PrepareImport(portabilitySource);
Check(preparedRules.Count == portabilitySource.Length, "import preparation preserves every rule for review");
Check(preparedRules.Take(3).SequenceEqual(portabilitySource.Take(3)), "portable import retains enabled and disabled states");
Check(preparedRules.Skip(3).All(r => !r.Enabled), "machine-specific imported rules default to disabled");
Check(preparedRules.Zip(portabilitySource).All(pair => pair.First with { Enabled = pair.Second.Enabled } == pair.Second), "import preparation retains IDs paths names kinds and inheritance");
Check(RulePortability.PrepareImport(portabilitySource, enableLocalRules: true).SequenceEqual(portabilitySource), "explicit local-rule confirmation preserves original enabled states");
Check(portabilitySource.SequenceEqual(sourceBefore), "sharing and import preparation never mutate input rules");
Check(!RulePortability.PrepareImport(portabilitySource, enableLocalRules: true).Single(r => r.Id == localPathRule.Id).Enabled, "explicit local confirmation does not enable an originally disabled rule");
var missingSnapshot = Snapshot(P(600, "unrelated"));
Check(missingSnapshot.Processes.Count(p => ProtectionPolicy.IsDirectlyWhitelisted(p, RulePortability.ForSharing(defaults))) == 0, "missing or not-running shared app yields zero matches without error");
var relocatedClash = ProcessIdentity.Classify(P(610) with { Name = "clash-verge.exe", Path = @"Z:\PortableApps\Proxy\clash-verge.exe" });
Check(policy.Evaluate(relocatedClash, Snapshot(relocatedClash), RulePortability.ForSharing(defaults)).Protected, "known application rule survives a different installation directory");
var relocatedPackage = ProcessIdentity.Classify(P(620) with { Name = "notepad.exe", Path = @"D:\AlternatePackages\notepad.exe", PackageFamilyName = "Microsoft.WindowsNotepad_8wekyb3d8bbwe" });
Check(ProtectionPolicy.IsDirectlyWhitelisted(relocatedPackage, [packageRule]), "stable package family matches regardless of installation path");

var testRoot = Environment.GetEnvironmentVariable("PROCESSKEEPER_TEST_ROOT") ?? Path.Combine(AppContext.BaseDirectory, "test-work");
var storeDirectory = Path.Combine(testRoot, "whitelist-" + Guid.NewGuid().ToString("N"));
var store = new WhitelistStore(storeDirectory);
Check(store.Load().Count == 0, "missing config yields an empty whitelist");
Check(!File.Exists(store.FilePath), "loading defaults does not write settings");
store.Save(defaults);
Check(store.Load().SequenceEqual(defaults), "configuration round trip");
var updated = defaults.Select(r => r.Value == "known:qq" ? r with { Enabled = false } : r).ToArray();
store.Save(updated);
Check(store.Load().Single(r => r.Value == "known:qq").Enabled == false, "disabled preference persisted");
Check(File.Exists(store.FilePath + ".bak"), "atomic replacement preserves previous configuration");
Check(Directory.GetFiles(storeDirectory, "*.tmp").Length == 0, "atomic save leaves no temporary files");
bool Throws(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
string exported = Path.Combine(storeDirectory, "portable-rules.json");
WhitelistStore.ExportToFile(exported, updated);
Check(WhitelistStore.ReadImport(exported).SequenceEqual(updated), "portable export and import round trip");
Check(store.Load().SequenceEqual(updated), "reading import does not modify active configuration");
var duplicate = updated[0] with { Id = "imported-duplicate", Name = "Incoming name", Value = updated[0].Value.ToUpperInvariant(), Enabled = !updated[0].Enabled };
var collision = Rule(RuleKind.ProcessName, "separate.exe") with { Id = updated[1].Id };
var merged = WhitelistStore.MergeRules(updated, [duplicate, collision]);
Check(merged.Count == updated.Length + 1, "merge deduplicates kind value inheritance ignoring case");
Check(merged[0] == updated[0], "merge keeps existing name and enabled preference");
Check(merged.Last().Id != collision.Id && merged.Select(r => r.Id).Distinct().Count() == merged.Count, "merge regenerates colliding imported ID");
Check(WhitelistStore.MergeRules([updated[0], duplicate], []).Count == 1, "merge also deduplicates existing semantic copies");
Check(WhitelistStore.MergeRules(updated, [duplicate with { IncludeDescendants = !duplicate.IncludeDescendants }]).Count == updated.Length + 1, "inheritance participates in merge identity");
Check(Throws(() => WhitelistStore.ReadImport(Path.Combine(storeDirectory, "absent.json"))), "missing import fails instead of loading defaults");
File.WriteAllText(exported, "{broken");
Check(Throws(() => WhitelistStore.ReadImport(exported)), "invalid import is rejected");
Check(store.Load().SequenceEqual(updated), "invalid import leaves active settings unchanged");
File.WriteAllText(exported, new string(' ', 1024 * 1024 + 1));
Check(Throws(() => WhitelistStore.ReadImport(exported)), "import file over one MiB is rejected");
var tooMany = Enumerable.Range(0, 1001).Select(i => Rule(RuleKind.ProcessName, $"app{i}.exe")).ToArray();
Check(Throws(() => WhitelistStore.ExportToFile(exported, tooMany)), "export over one thousand rules is rejected");
Check(File.ReadAllText(exported).Length == 1024 * 1024 + 1, "failed export preserves previous destination");
WhitelistStore.ExportToFile(exported, updated);
Check(Throws(() => WhitelistStore.ExportToFile(exported, [Rule(RuleKind.ProcessName, "app.exe") with { Name = new string('a', 1024 * 1024) } ])), "oversized serialized export is rejected");
Check(WhitelistStore.ReadImport(exported).SequenceEqual(updated), "oversized export does not replace prior file");
Check(Throws(() => store.Save([Rule(RuleKind.Directory, "relative")])), "reject relative directory rule");
Check(Throws(() => store.Save([Rule(RuleKind.ProcessName, "*.exe")])), "reject wildcard process name rule");
Check(Throws(() => store.Save([defaults[0], defaults[0]])), "reject duplicate rule IDs");
File.WriteAllText(store.FilePath, "{broken");
Check(Throws(() => store.Load()), "corrupt configuration fails closed");
Check(File.ReadAllText(store.FilePath) == "{broken", "corrupt original is untouched");
File.WriteAllText(store.FilePath, "{\"Version\":2,\"Rules\":[]}");
Check(Throws(() => store.Load()), "unknown future schema fails closed");
File.WriteAllText(store.FilePath, "{\"Version\":1}");
Check(Throws(() => store.Load()), "missing rule list fails closed");
File.WriteAllText(store.FilePath, "{\"Version\":1,\"Rules\":[{\"Name\":\"Bad\",\"Value\":\"known:qq\"}]}");
Check(Throws(() => store.Load()), "partially missing rule fields fail closed");
File.WriteAllText(store.FilePath, "{\"Version\":1,\"Version\":1,\"Rules\":[]}");
Check(Throws(() => store.Load()), "duplicate JSON properties fail closed");
// Delete only individual files in the uniquely created fixture folder, never recurse over a user path.
foreach (var file in Directory.GetFiles(storeDirectory)) File.Delete(file);
Directory.Delete(storeDirectory);

// The only process ever closed by this test is a child that this exact invocation creates.
if (args.Contains("--live-close-test"))
{
    using var identity = WindowsIdentity.GetCurrent();
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--owned-test-child");
    using var child = Process.Start(start) ?? throw new InvalidOperationException("Unable to start owned test child");
    try
    {
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "OWNED_TEST_CHILD_READY", "owned test child handshake");
        var target = new ProcessRecord
        {
            Id = child.Id, ParentId = Environment.ProcessId, Name = Path.GetFileName(child.MainModule!.FileName),
            Path = child.MainModule.FileName, SessionId = child.SessionId, OwnerSid = identity.User!.Value,
            StartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks
        };
        var closer = new ProcessCloser();
        ProtectionDecision Allow(ProcessRecord _) => new(false, "Owned test child");
        var result = await closer.CloseAsync([target with { StartTimeUtcTicks = target.StartTimeUtcTicks + 1 }], true, Allow);
        Check(!result[0].Success && !child.HasExited, "mismatched creation time cannot close owned child");
        result = await closer.CloseAsync([target with { Path = @"C:\Wrong\same.exe" }], true, Allow);
        Check(!result[0].Success && !child.HasExited, "mismatched image path cannot close owned child");
        result = await closer.CloseAsync([target with { OwnerSid = "S-1-5-18" }], true, Allow);
        Check(!result[0].Success && !child.HasExited, "mismatched owner cannot close owned child");
        result = await closer.CloseAsync([target], true, _ => new(true, "Changed rule"));
        Check(!result[0].Success && !child.HasExited, "current whitelist recheck prevents close");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        result = await closer.CloseAsync([target], true, Allow, cancelled.Token);
        Check(!result[0].Success && !child.HasExited, "cancelled close leaves owned child alive");
        result = await closer.CloseAsync([target], false, Allow);
        Check(!result[0].Success && !child.HasExited, "no-window child survives graceful-only mode");
        int calls = 0;
        result = await closer.CloseAsync([target], true, _ => ++calls >= 3 ? new(true, "Changed before force") : Allow(target));
        Check(!result[0].Success && !child.HasExited, "rule changed during grace wait cancels force");
        result = await closer.CloseAsync([target], true, Allow);
        Check(result[0].Success && child.HasExited, "explicit force terminates only owned verified child");
    }
    finally
    {
        if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync(); }
    }
}

Console.WriteLine($"PASS: {assertions} assertions; no existing user process was closed.");
