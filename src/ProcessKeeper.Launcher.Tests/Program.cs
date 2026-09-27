using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ProcessKeeper.Launcher;

if (args is ["--bootstrap-text-only"]) { BootstrapTextTests.Run(); return; }
if (args is ["--gate-child-exit"]) return;
if (args is ["--gate-child-wait"]) { Console.ReadLine(); return; }

try
{
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); } catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException) { Check(true, name); return; }
    throw new Exception("FAIL: accepted " + name);
}
StartupElevationTests.Run(Check);
byte[] MakeZip(params (string Name, string Text)[] files)
{
    using var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        foreach (var item in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(item.Name).Open(), new UTF8Encoding(false));
            writer.Write(item.Text);
        }
    return stream.ToArray();
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
PayloadContract Contract(byte[] zip, params (string Name, string Text)[] files)
{
    var manifest = new PayloadManifest
    {
        FormatVersion = 1, EntryPoint = "ProcessKeeper.exe", ZipSha256 = Hash(zip),
        Files = files.Select(f => new PayloadFile { Path = f.Name, Length = Encoding.UTF8.GetByteCount(f.Text), Sha256 = Hash(Encoding.UTF8.GetBytes(f.Text)) }).ToList()
    };
    var json = JsonSerializer.SerializeToUtf8Bytes(manifest, PayloadJsonContext.Default.PayloadManifest);
    return PayloadContract.Read(new MemoryStream(json));
}

var fixtureRoot = Path.GetFullPath(Path.Combine(args.Single(), "run-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixtureRoot);
var contents = new[] { ("ProcessKeeper.exe", "INTENTIONALLY NOT AN EXECUTABLE"), ("licenses/许可.txt", "license evidence") };
var zip = MakeZip(contents);
var contract = Contract(zip, contents);
contract.VerifyArchiveHash(new MemoryStream(zip));
Check(true, "embedded ZIP SHA verified");
var version = Path.Combine(fixtureRoot, contract.Hash);
CacheStore.EnsurePlainDirectory(version);
using (var held = CacheStore.AcquireLock(version, TimeSpan.FromSeconds(1), restrictedSecurity: false))
{
    Reject(() => { using var other = CacheStore.AcquireLock(version, TimeSpan.FromMilliseconds(10), restrictedSecurity: false); }, "cross-process-compatible file lock excludes another opener");
    var app = CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false);
    Check(CacheStore.ValidateCache(app, contract, restrictedSecurity: false), "initial extraction passes all file hashes");
    var exe = Path.Combine(app, "ProcessKeeper.exe");
    var timestamp = File.GetLastWriteTimeUtc(exe);
    Check(CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false) == app && File.GetLastWriteTimeUtc(exe) == timestamp, "valid cache reused without extraction");
    using (var verified = CacheStore.OpenVerifiedFiles(app, contract, restrictedSecurity: false))
        Reject(() => File.WriteAllText(exe, "tamper"), "verification handles prevent replacement before launch");
    File.WriteAllText(exe, "tamper");
    Check(!CacheStore.ValidateCache(app, contract, restrictedSecurity: false), "changed file rejected");
    CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false);
    Check(File.ReadAllText(exe) == contents[0].Item2, "corrupt cache completely rebuilt");
    File.WriteAllText(Path.Combine(app, "injected.dll"), "unlisted");
    Check(!CacheStore.ValidateCache(app, contract, restrictedSecurity: false), "unlisted DLL rejected");
    CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false);
    Check(!File.Exists(Path.Combine(app, "injected.dll")), "unlisted DLL removed by complete rebuild");
    File.Delete(Path.Combine(app, "licenses", "许可.txt"));
    Check(!CacheStore.ValidateCache(app, contract, restrictedSecurity: false), "missing license rejected");
    CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false);
    Check(File.Exists(Path.Combine(app, "licenses", "许可.txt")), "nested Unicode file restored");
    Check(Directory.GetDirectories(version, ".staging-*").Length == 0, "no staging directory left after success");
}

foreach (var bad in new[] { "../outside", "/absolute", "C:/escape", "file:stream", "a\\b", "a//b", "a/../b", "CON.exe", "LPT1.txt", "a./b", "a /b", "a/" })
    Reject(() => PayloadContract.ValidateRelativePath(bad), "reject unsafe path " + bad);
Reject(() => Contract(zip, ("ProcessKeeper.exe", "a"), ("processkeeper.EXE", "b")), "case-insensitive duplicate manifest paths rejected");
Reject(() => Contract(zip, ("ProcessKeeper.exe", "a"), ("folder", "b"), ("folder/file", "c")), "file/directory prefix collision rejected");
Reject(() => contract.VerifyArchiveHash(new MemoryStream([1, 2, 3])), "bad embedded ZIP hash rejected");

var badZip = MakeZip(("../escaped.exe", "not executable"), ("licenses/许可.txt", contents[1].Item2));
var badContract = Contract(badZip, contents);
var badVersion = Path.Combine(fixtureRoot, badContract.Hash);
Directory.CreateDirectory(badVersion);
Reject(() => CacheStore.Prepare(badVersion, new MemoryStream(badZip), badContract, restrictedSecurity: false), "ZIP traversal rejected before extraction");
Check(!File.Exists(Path.Combine(fixtureRoot, "escaped.exe")), "ZIP traversal never writes outside version");
Check(Directory.GetDirectories(badVersion, ".staging-*").Length == 0, "failed extraction staging isolated");

var unrelated = Path.Combine(fixtureRoot, "other-version");
Directory.CreateDirectory(unrelated);
File.WriteAllText(Path.Combine(unrelated, "sentinel.txt"), "preserve");
var validApp = Path.Combine(version, "App");
try
{
    Directory.CreateSymbolicLink(Path.Combine(validApp, "foreign-link"), unrelated);
    Check(!CacheStore.ValidateCache(validApp, contract, restrictedSecurity: false), "cache reparse point rejected");
    CacheStore.Prepare(version, new MemoryStream(zip), contract, restrictedSecurity: false);
    Check(File.ReadAllText(Path.Combine(unrelated, "sentinel.txt")) == "preserve", "rebuild isolates old tree without following link");
}
catch (UnauthorizedAccessException) { Console.WriteLine("SKIP symbolic-link fixture: privilege unavailable"); }
Check(File.ReadAllText(Path.Combine(unrelated, "sentinel.txt")) == "preserve", "unrelated version directory preserved");
Check(Directory.GetDirectories(version, ".invalid-*").Length >= 3, "bad caches quarantined without recursive deletion");
var secureDescriptor = CacheSecurity.NewDirectorySecurity();
CacheSecurity.ValidateDescriptor(secureDescriptor, isDirectory: true);
Check(true, "administrator-owned protected two-account DACL accepted");
var personalOwner = CacheSecurity.NewDirectorySecurity();
personalOwner.SetOwner(WindowsIdentity.GetCurrent().User!);
Reject(() => CacheSecurity.ValidateDescriptor(personalOwner, true), "personal owner rejected despite restrictive DACL");
var userWritable = CacheSecurity.NewDirectorySecurity();
userWritable.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
Reject(() => CacheSecurity.ValidateDescriptor(userWritable, true), "additional user write ACE rejected");
var unprotected = CacheSecurity.NewDirectorySecurity();
unprotected.SetAccessRuleProtection(false, false);
Reject(() => CacheSecurity.ValidateDescriptor(unprotected, true), "unprotected inheritable DACL rejected");
var denyingDescriptor = CacheSecurity.NewDirectorySecurity();
denyingDescriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Deny));
Reject(() => CacheSecurity.ValidateDescriptor(denyingDescriptor, true), "unexpected deny ACE rejected conservatively");
Reject(() => CacheSecurity.AssertParentCannotReplaceChildren(personalOwner), "ancestor owner with implicit WRITE_DAC rejected");
var unsafeAncestor = CacheSecurity.NewDirectorySecurity();
unsafeAncestor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
Reject(() => CacheSecurity.AssertParentCannotReplaceChildren(unsafeAncestor), "ancestor allowing ordinary users to replace children rejected");
var systemAncestor = new DirectoryInfo(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
while (systemAncestor is not null)
{
    CacheSecurity.AssertParentCannotReplaceChildren(systemAncestor.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
    systemAncestor = systemAncestor.Parent;
}
Check(true, "real ProgramData ancestor owners and ACLs accepted read-only");
using var embeddedManifest = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ProcessKeeper.Payload.json");
if (embeddedManifest is not null)
{
    using var embeddedZip = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ProcessKeeper.Payload.zip")!;
    var embeddedContract = PayloadContract.Read(embeddedManifest);
    embeddedContract.VerifyArchiveHash(embeddedZip);
    var embeddedVersion = Path.Combine(fixtureRoot, "embedded-payload");
    Directory.CreateDirectory(embeddedVersion);
    var embeddedApp = CacheStore.Prepare(embeddedVersion, embeddedZip, embeddedContract, restrictedSecurity: false);
    Check(CacheStore.ValidateCache(embeddedApp, embeddedContract, restrictedSecurity: false), "NativeAOT embedded resource streams and ZIP extraction verified without launch");
}
await ElevationGateTests.RunAsync(fixtureRoot, Check, Reject);
Console.WriteLine($"{passed} checks passed. No payload executable or UAC was launched; owned no-op lifetime helpers only. Fixtures: {fixtureRoot}");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    Environment.ExitCode = 1;
}
