using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ProcessKeeper.Core;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
void Invalid(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
ProcessRecord Process(string path, int id = 42) => new() { Id = id, Name = Path.GetFileName(path), Path = path, ApplicationKey = "fixture:" + id, Company = "Microsoft Corporation" };
ApplicationGroup Group(params string[] paths) => new() { Key = "fixture:group", Name = "Untrusted Microsoft display name", Company = "Microsoft Corporation", Processes = paths.Select((path, index) => Process(path, index + 1)).ToArray() };
InstalledApplication Installed(params string[] paths) => new() { Id = "fixture:installed", Name = "Microsoft alleged", Publisher = "Microsoft Corporation", Executables = paths.Select(path => new InstalledExecutable { Path = path }).ToArray() };
AutorunEntry Autorun(string path, AutorunSourceKind source = AutorunSourceKind.RegistryRun) => new() { Id = "fixture:autorun", Name = "Microsoft alleged", SourceKind = source, TargetPath = path, Command = "\"" + path + "\"", IsSystem = true };

string Manifest(string id = "123", string name = "Example Game", string directory = "Example", string state = "4") =>
    "\"AppState\" { \"appid\" \"" + id + "\" \"name\" \"" + name + "\" \"installdir\" \"" + directory + "\" \"StateFlags\" \"" + state + "\" }";
var parsed = SteamLibraryCatalog.ReadAppManifest(Manifest(), @"D:\SteamLibrary", "appmanifest_123.acf");
Check(parsed is { AppId: 123, Name: "Example Game", InstallDirectory: @"D:\SteamLibrary\steamapps\common\Example" }, "Steam app ID and install path originate in its manifest");
foreach (var id in new[] { "0", "-1", "123x", "00123", "4294967296" }) Check(SteamLibraryCatalog.ReadAppManifest(Manifest(id), @"D:\SteamLibrary", "appmanifest_" + id + ".acf") is null, "invalid Steam app ID rejected " + id);
foreach (var directory in new[] { "..", ".", @"..\outside", "D:\\outside", "Game.", " Game", "Game:stream" }) Check(SteamLibraryCatalog.ReadAppManifest(Manifest(directory: directory), @"D:\SteamLibrary", "appmanifest_123.acf") is null, "invalid Steam install directory rejected");
Check(SteamLibraryCatalog.ReadAppManifest(Manifest(), @"D:\SteamLibrary", "appmanifest_456.acf") is null, "mismatching manifest filename rejected");
Check(SteamLibraryCatalog.ReadAppManifest(Manifest(state: "2"), @"D:\SteamLibrary", "appmanifest_123.acf") is null, "incomplete Steam installation stays independent");
Check(SteamLibraryCatalog.ReadAppManifest(Manifest(), @"\\server\library", "appmanifest_123.acf") is null, "network Steam library not read");
var libraries = SteamLibraryCatalog.ReadLibraryFolders("// comment\n\"libraryfolders\" { \"0\" \"C:\\\\Steam\" \"1\" { \"path\" \"D:\\\\SteamLibrary\" \"apps\" { \"123\" \"10\" } } }");
Check(libraries.Count == 2 && libraries.Contains(@"D:\SteamLibrary"), "old and new Steam library formats accepted");
Invalid(() => SteamLibraryCatalog.ReadLibraryFolders("\"libraryfolders\" { \"0\" \"C:\\Steam\" \"0\" \"D:\\Steam\" }"), "duplicate VDF keys rejected");
Invalid(() => SteamLibraryCatalog.ReadLibraryFolders("\"libraryfolders\" {"), "truncated VDF rejected");
Invalid(() => SteamLibraryCatalog.ReadLibraryFolders(new string('x', 1024 * 1024 + 1)), "VDF size bounded");

string EpicJson(string path = @"D:\Epic\Example", string launch = @"Binaries\Example.exe", bool incomplete = false) => JsonSerializer.Serialize(new { AppName = "epic-id", DisplayName = "Epic Example", InstallLocation = path, LaunchExecutable = launch, bIsIncompleteInstall = incomplete });
var epic = GamePlatformCatalog.ReadEpicManifest(EpicJson());
Check(epic?.Platform.Id == "epic" && epic.Id == "epic-id", "Epic manifest establishes platform and stable ID");
foreach (var launch in new[] { @"..\outside.exe", @"D:\outside.exe", "http://host/file.exe", @"bin\..\file.exe", "file.dll", "file.exe:stream" })
    Check(GamePlatformCatalog.ReadEpicManifest(EpicJson(launch: launch)) is null, "unsafe Epic launch path rejected");
Check(GamePlatformCatalog.ReadEpicManifest(EpicJson(incomplete: true)) is null, "incomplete Epic install rejected");
Check(GamePlatformCatalog.ReadEpicManifest(EpicJson(path: @"D:\")) is null, "broad Epic root rejected");
Check(GamePlatformCatalog.ReadEpicManifest(EpicJson().Replace("\"bIsIncompleteInstall\":false", "\"bIsIncompleteInstall\":false,\"bIsIncompleteInstall\":false")) is null, "duplicate Epic fields rejected");
Invalid(() => GamePlatformCatalog.ReadEpicManifest(new string('x', 1024 * 1024 + 1)), "Epic manifest size bounded");
Check(GamePlatformCatalog.ReadRegisteredGame(GamePlatformCatalog.Ubisoft, "93", "Game", @"D:\Ubisoft\Game")?.Platform.Id == "ubisoft", "Ubisoft numeric installation registration accepted");
Check(GamePlatformCatalog.ReadRegisteredGame(GamePlatformCatalog.Ubisoft, "account", "Game", @"D:\Ubisoft\Game") is null, "Ubisoft non-installation key not accepted");
Check(GamePlatformCatalog.ReadRegisteredGame(GamePlatformCatalog.Ea, "Example", "Game", @"D:\EA Games\Game")?.Platform.Id == "ea", "EA game installation registration accepted");
Check(GamePlatformCatalog.ReadRegisteredGame(GamePlatformCatalog.Ea, "Example", "Game", @"D:\") is null, "broad EA registration rejected");

var steamGame = new GameCatalogEntry("123", "Example Game", @"D:\SteamLibrary\steamapps\common\Example", GamePlatformCatalog.Steam);
var pathGame = steamGame.InstallDirectory + @"\bin\game.exe";
var clientPath = @"C:\Games\Steam\steam.exe";
var microsoftPath = @"C:\Verified\Microsoft.exe";
var otherPath = @"C:\Other\Other.exe";
var catalog = new ApplicationDisplayCatalog([steamGame, epic!], [new(GamePlatformCatalog.Steam, clientPath)], [microsoftPath]);
var group = Group(pathGame); var original = group.Processes;
Check(catalog.FindGame(group)?.Id == "123" && catalog.FindGamePlatform(group)?.Id == "steam", "running game display uses manifest ownership");
Check(catalog.FindGame(Installed(pathGame))?.Id == "123", "installed game display uses same manifest evidence");
Check(catalog.FindGame(Autorun(pathGame))?.Id == "123", "direct autorun game uses same display evidence");
Check(catalog.FindGame(Group(steamGame.InstallDirectory + @"Copy\game.exe")) is null, "path prefix alone does not claim a game");
Check(catalog.FindGame(Group(pathGame, otherPath)) is null, "mixed application process group stays independent");
Check(catalog.FindGame(Installed(pathGame, otherPath)) is null, "mixed installed executable set stays independent");
Check(catalog.FindGame(Group("")) is null, "unknown executable stays independent");
Check(catalog.FindClientPlatform(Group(clientPath))?.Id == "steam", "registered Steam client path recognized");
Check(catalog.FindClientPlatform(Group(@"C:\Unregistered\steam.exe")) is null, "Steam name alone not a client identity");
Check(catalog.FindClientPlatform(Group(clientPath, pathGame)) is null, "client with game process cannot be collapsed as only client");
Check(catalog.FindClientPlatform(Group(clientPath, @"C:\Games\Steam\steamapps\common\Unregistered\game.exe")) is null, "unregistered Steam library content is not treated as a launcher component");
Check(catalog.FindGamePlatform(Group(clientPath))?.Id == "steam", "client and games share display platform only");
Check(ReferenceEquals(group.Processes, original) && group.Key == "fixture:group" && group.Processes.Count == 1, "display lookup leaves process group and close identity unchanged");
Check(new RuleProcessMatcher(new(DateTimeOffset.UtcNow, [Process(pathGame)], [])).Match(new WhitelistRule { Kind = RuleKind.ExecutablePath, Value = clientPath }).Count == 0, "retaining Steam does not retain a manifest game");
var conflict = new ApplicationDisplayCatalog([steamGame, steamGame with { Platform = GamePlatformCatalog.Ea }], [], []);
Check(conflict.FindGame(Group(pathGame)) is null, "cross-platform conflicting installation remains independent");
var overlap = new ApplicationDisplayCatalog([steamGame, steamGame with { Id = "456", InstallDirectory = steamGame.InstallDirectory + @"\bin" }], [], []);
Check(overlap.FindGame(Group(pathGame)) is null, "overlapping game roots remain ambiguous");
Check(catalog.IsMicrosoft(Group(microsoftPath)) && catalog.IsMicrosoft(Installed(microsoftPath)), "verified Microsoft executable works for running and installed displays");
Check(!catalog.IsMicrosoft(Group(otherPath)) && !catalog.IsMicrosoft(Installed(otherPath)), "Microsoft company and publisher strings never sufficient");
Check(!catalog.IsMicrosoft(Group(microsoftPath, otherPath)) && !catalog.IsMicrosoft(Installed(microsoftPath, otherPath)), "mixed third-party application remains visible");
Check(!catalog.IsMicrosoft(Group()) && !catalog.IsMicrosoft(Installed()), "empty application never hidden");
Check(catalog.IsMicrosoft(Autorun(microsoftPath)), "direct verified Microsoft autorun recognized");
Check(!catalog.IsMicrosoft(Autorun(otherPath)), "IsSystem protection flag does not imply Microsoft ownership");
Check(catalog.IsMicrosoft(Autorun(otherPath) with { Ownership = AutorunOwnership.Windows }), "verified Windows component attribution reused");
foreach (var kind in new[] { AutorunSourceKind.ScheduledTask, AutorunSourceKind.StartupFolder, AutorunSourceKind.WmiSubscription, AutorunSourceKind.AdvancedRegistry })
    Check(!catalog.IsMicrosoft(Autorun(microsoftPath, kind)), "multi-action or indirect autorun remains unknown " + kind);
var hosts = new ApplicationDisplayCatalog([], [], [@"C:\Windows\System32\cmd.exe"]);
Check(!hosts.IsMicrosoft(Autorun(@"C:\Windows\System32\cmd.exe")), "shared command host cannot hide third-party autorun");
Check(!hosts.IsMicrosoft(Group(@"C:\Windows\System32\cmd.exe")) && !hosts.IsMicrosoft(Installed(@"C:\Windows\System32\cmd.exe")), "shared host alone cannot hide third-party content in app lists");
Check(ApplicationDisplayCatalog.Empty.FindGame(group) is null && !ApplicationDisplayCatalog.Empty.IsMicrosoft(group), "empty initial catalog preserves visibility");
var largeCatalog = new ApplicationDisplayCatalog(Enumerable.Range(1, 9000).Select(index => steamGame with { Id = index.ToString(), InstallDirectory = @"D:\Games\Game" + index }),
    Enumerable.Range(1, 100).Select(index => new GamePlatformClient(GamePlatformCatalog.Steam, @"C:\Clients\Client" + index + @"\steam.exe")), [], Enumerable.Range(1, 100).Select(index => "warning" + index));
Check(largeCatalog.Games.Count == 8192 && largeCatalog.Clients.Count == 32 && largeCatalog.Warnings.Count == 40, "catalog input and retained diagnostics are bounded");
var lookupClock = System.Diagnostics.Stopwatch.StartNew();
for (int index = 1; index <= 8192; index++) if (largeCatalog.FindGame(Group(@"D:\Games\Game" + index + @"\bin\game.exe"))?.Id != index.ToString()) throw new Exception("large catalog lookup");
Check(lookupClock.Elapsed < TimeSpan.FromSeconds(5), "8192 game lookups use directory index rather than full scans");

var subjectMethod = typeof(MicrosoftPublisherProbe).GetMethod("HasMicrosoftOrganization", BindingFlags.Static | BindingFlags.NonPublic)!;
bool Subject(string dn) => (bool)subjectMethod.Invoke(null, [new X500DistinguishedName(dn).RawData])!;
Check(Subject("CN=Fixture, O=Microsoft Corporation, C=US"), "Microsoft subject organization exact match");
Check(Subject("CN=Fixture, O=microsoft corporation"), "organization match case-insensitive");
foreach (var dn in new[] { "CN=Microsoft Corporation, O=Other", "CN=Fixture, O=Microsoft Corporation Evil", "CN=Fixture, O=Microsoft", "CN=Fixture, O=Microsoft Corporation, O=Other", "CN=Fixture, O=Microsoft Corporation, O=Microsoft Corporation" })
    Check(!Subject(dn), "forged or duplicate organization rejected");
Check(!(bool)subjectMethod.Invoke(null, [new byte[] { 0x30, 0x80, 0, 0 }])!, "indefinite DER name rejected");
Check(!Subject("CN=\"Fake\nO=Microsoft Corporation\", O=Other"), "organization text embedded in another DN attribute not accepted");

var fake = new PublisherProbe(); var now = DateTimeOffset.UtcNow; int scans = 0;
var service = new ApplicationDisplayService(_ => { scans++; return new([steamGame], [new(GamePlatformCatalog.Steam, clientPath)], []); }, fake, () => now);
fake.Paths[microsoftPath] = (new(1, 1, 10, 20, 30), true);
var snap = new ProcessSnapshot(now, [Process(microsoftPath)], [Group(microsoftPath)]);
Check(service.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)), "background service accepts verified publisher");
Check(fake.Verifications == 1 && scans == 1, "first capture performs single publisher and platform read");
Check(service.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)) && fake.Verifications == 1 && scans == 1, "repeated capture uses identity-bound cache");
fake.Paths[microsoftPath] = (new(1, 2, 10, 20, 30), false);
Check(!service.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)) && fake.Verifications == 2, "same size and timestamp replacement detected by file ID");
fake.Paths.Remove(microsoftPath);
Check(!service.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)), "deleted cached file stays visible");
now += TimeSpan.FromMinutes(2); service.Capture(ProcessSnapshot.Empty, [], []); Check(scans == 2, "platform registration periodically refreshed");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel(); var previous = fake.Verifications;
    try { service.Capture(snap, [], [], cancellation.Token); throw new Exception("not canceled"); } catch (OperationCanceledException) { Check(fake.Verifications == previous, "pre-cancelled capture does not probe files"); }
}
var changing = new PublisherProbe(); changing.Paths[microsoftPath] = (new(1, 1, 10, 20, 30), true);
changing.DuringVerification = () => changing.Paths[microsoftPath] = (new(1, 2, 10, 20, 30), true);
var changingService = new ApplicationDisplayService(_ => new([], [], []), changing);
Check(!changingService.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)), "replacement during verification cannot become hidden");
var bounded = new PublisherProbe();
var many = Enumerable.Range(1, 300).Select(index => Process(@"C:\Fixtures\Microsoft" + index + ".exe", index)).ToArray();
foreach (var process in many) bounded.Paths[process.Path] = (new(1, (ulong)process.Id, 10, 20, 30), true);
var boundedService = new ApplicationDisplayService(_ => new([], [], []), bounded);
boundedService.Capture(new(now, many, []), [], []);
Check(bounded.Verifications <= 128 && bounded.Verifications > 0, "publisher starts bounded per capture");
var beforeMore = bounded.Verifications; boundedService.Capture(new(now, many, []), [], []);
Check(bounded.Verifications > beforeMore && bounded.Verifications <= beforeMore + 128, "later capture continues remaining publisher checks without rechecking cached signatures");
using (var hold = new ManualResetEventSlim(false))
{
    var slow = new PublisherProbe { DuringVerification = () => hold.Wait() }; slow.Paths[microsoftPath] = (new(1, 1, 10, 20, 30), true);
    var slowService = new ApplicationDisplayService(_ => new([], [], []), slow);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    Check(!slowService.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)) && clock.Elapsed < TimeSpan.FromSeconds(2), "slow native provider returns unknown within wait budget");
    slowService.Capture(snap, [], []); Check(slow.Verifications == 1, "timed out provider does not accumulate native workers");
    hold.Set(); SpinWait.SpinUntil(() => slow.Finished == 1, 3000);
    Check(slowService.Capture(snap, [], []).IsMicrosoft(Group(microsoftPath)), "late completed result applies only after identity recheck");
}

var fixture = Path.Combine(Path.GetTempPath(), "ProcessKeeper-display-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
try
{
    var steamRoot = Path.Combine(fixture, "Steam"); var appDirectory = Path.Combine(steamRoot, @"steamapps\common\Example"); Directory.CreateDirectory(appDirectory);
    File.WriteAllText(Path.Combine(steamRoot, "steam.exe"), "fixture, never executed");
    File.WriteAllText(Path.Combine(steamRoot, @"steamapps\appmanifest_123.acf"), Manifest());
    var index = new SteamLibraryCatalog(() => [steamRoot]).Scan();
    Check(index.Games.Count == 1 && index.IsClientPath(Path.Combine(steamRoot, "steam.exe")), "isolated Steam files discover installed game and client");
    Check(index.Find(Path.Combine(appDirectory, "game.exe"))?.AppId == 123, "disk-backed manifest matches nested executable path");
    File.WriteAllText(Path.Combine(steamRoot, @"steamapps\appmanifest_456.acf"), Manifest("456", directory: "Absent"));
    Check(new SteamLibraryCatalog(() => [steamRoot]).Scan().Games.Count == 1, "stale missing installed game folder not grouped");
    var native = new MicrosoftPublisherProbe(); var file = Path.Combine(fixture, "not-signed.exe"); File.WriteAllText(file, "fixture");
    Check(native.ReadIdentity(file) is not null && !native.IsMicrosoft(file, default), "real native file handle identifies unsigned fixture but never trusts it");
    Check(native.ReadIdentity(Path.Combine(fixture, "absent.exe")) is null, "missing file identity fails closed");
    if (args.Length == 2 && args[0] == "--native-path")
    {
        var copy = Path.Combine(fixture, "signed-copy.exe"); File.Copy(args[1], copy);
        Check(native.IsMicrosoft(copy, default), "real Windows trust provider confirms Microsoft signer of isolated signed copy");
    }
}
finally { Directory.Delete(fixture, true); }
foreach (var language in new[] { "en", "zh-Hans", "zh-Hant" }) { L.Language = language; Check(L.T("部分发布者仍未核实，相关应用暂时保留显示。").Length > 0, "publisher uncertainty localized " + language); }
Console.WriteLine($"PASS: {count} display assertions | CLR bitness {IntPtr.Size * 8}");

sealed class PublisherProbe : IMicrosoftPublisherProbe
{
    public Dictionary<string, (PublisherFileIdentity Identity, bool Microsoft)> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Action? DuringVerification; public int Verifications, Finished;
    public PublisherFileIdentity? ReadIdentity(string path) => Paths.TryGetValue(path, out var value) ? value.Identity : null;
    public bool IsMicrosoft(string path, CancellationToken token)
    { Interlocked.Increment(ref Verifications); try { DuringVerification?.Invoke(); return Paths.TryGetValue(path, out var value) && value.Microsoft; } finally { Interlocked.Increment(ref Finished); } }
}
