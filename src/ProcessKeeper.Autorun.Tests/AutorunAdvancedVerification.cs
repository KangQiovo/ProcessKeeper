using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ProcessKeeper.Core;

internal static class AutorunAdvancedVerification
{
    internal static void Run(Action<bool, string> check)
    {
        const string foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        const string startup = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
        const string desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XDocument Manifest(string executable = "app.exe", string enabled = "true", string extensionNamespace = startup, int count = 1)
        {
            XNamespace ns = foundation, extension = extensionNamespace;
            var declaration = new XElement(extension + "Extension", new XAttribute("Category", "windows.startupTask"),
                new XAttribute("Executable", executable), Enumerable.Range(0, count).Select(number => new XElement(extension + "StartupTask",
                    new XAttribute("TaskId", "FixtureStartup" + number), new XAttribute("Enabled", enabled))));
            var app = new XElement(ns + "Application", new XAttribute("Id", "FixtureApp"), new XAttribute("Executable", "fallback.exe"),
                new XElement(ns + "Extensions", declaration));
            return new(new XElement(ns + "Package", new XElement(ns + "Applications", app)));
        }
        IReadOnlyList<AutorunEntry> Parse(XDocument document, CancellationToken token = default)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)));
            return AutorunAdvancedScanner.ReadPackageStartupManifest(stream, "Fixture_abc", "Fixture app", @"Q:\ManifestFixture\Package", token);
        }
        var entry = Parse(Manifest()).Single();
        check(entry.SourceKind == AutorunSourceKind.PackagedStartup && !entry.CanChange && entry.Enabled is null,
            "packaged startup declarations are read-only and do not infer current state from Enabled=true");
        check(entry.TargetPath == @"Q:\ManifestFixture\Package\app.exe" && entry.Location.Contains("Fixture_abc!FixtureApp") && entry.Name.Contains("FixtureStartup0"),
            "startup declaration preserves package/app/task ownership and its declared package-relative executable");
        check(Parse(Manifest(enabled: "false")).Single().Enabled is null,
            "manifest Enabled=false also cannot establish the user's actual Windows startup choice");
        check(Parse(Manifest(extensionNamespace: desktop)).Count == 1, "documented desktop startup namespace is recognized");
        check(Parse(Manifest(extensionNamespace: "urn:untrusted-lookalike")).Count == 0,
            "a same-named extension in an unknown XML namespace does not become a startup declaration");
        foreach (var invalid in new[] { @"..\escape.exe", @"C:\elsewhere.exe", @"\\server\share\payload.exe", "stream.exe:payload", "folder./app.exe", "folder /app.exe", "folder//app.exe" })
            check(Parse(Manifest(invalid)).Single().TargetPath.Length == 0,
                "unsafe manifest executable has no resolved file target: " + invalid);
        check(Parse(Manifest(count: 120)).Count == 100, "per-package startup declarations are capped at 100");
        var duplicate = Manifest(count: 2);
        duplicate.Descendants(XName.Get("StartupTask", startup)).Last().SetAttributeValue("TaskId", "FixtureStartup0");
        check(Parse(duplicate).Count == 1, "duplicate task identifiers do not create duplicate inventory rows");
        var fallback = Manifest();
        fallback.Descendants(XName.Get("Extension", startup)).Single().Attribute("Executable")!.Remove();
        check(Parse(fallback).Single().TargetPath.EndsWith(@"\fallback.exe", StringComparison.Ordinal),
            "an extension without an executable uses its parent Application declaration");
        var unrelated = Manifest();
        unrelated.Descendants(XName.Get("Extension", startup)).Single().SetAttributeValue("Category", "windows.fileTypeAssociation");
        check(Parse(unrelated).Count == 0, "unrelated package extensions are not misreported as logon startup tasks");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var cancellationCaught = false;
        try { Parse(Manifest(), cancelled.Token); } catch (OperationCanceledException) { cancellationCaught = true; }
        check(cancellationCaught, "package parsing honors cancellation before reading the manifest");
        var rejected = false;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE Package [<!ENTITY ex SYSTEM 'file:///Q:/never-read.txt'>]><Package xmlns='" + foundation + "'>&ex;</Package>"));
            AutorunAdvancedScanner.ReadPackageStartupManifest(stream, "fixture", "fixture", @"Q:\Fixture");
        }
        catch (XmlException) { rejected = true; }
        check(rejected, "manifest parser rejects DTD and external-entity resolution");
        rejected = false;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<Package xmlns='" + foundation + "'><Data>" + new string('x', 4 * 1024 * 1024) + "</Data></Package>"));
            AutorunAdvancedScanner.ReadPackageStartupManifest(stream, "fixture", "fixture", @"Q:\Fixture");
        }
        catch (XmlException) { rejected = true; }
        check(rejected, "manifest parser enforces the four-megabyte character bound");
        var notices = new List<string>();
        check(AutorunAdvancedScanner.Scan(notices, CancellationToken.None, () => false).Count == 0 && notices.Count > 0,
            "an exhausted advanced-source budget returns immediately without accessing native sources");
        cancellationCaught = false;
        try { AutorunAdvancedScanner.Scan([], cancelled.Token, () => true); }
        catch (OperationCanceledException) { cancellationCaught = true; }
        check(cancellationCaught, "a pre-cancelled advanced inventory performs no native reads");

        var relative = typeof(AutorunAdvancedScanner).GetMethod("Relative", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Normalize(string reference) => (string)relative.Invoke(null, [reference, @"root\subscription"])!;
        const string consumer = "CommandLineEventConsumer.Name=\"Fixture\"";
        check(Normalize(consumer) == consumer && Normalize(@"\\.\root\subscription:" + consumer) == consumer,
            "local WMI references retain their consumer relationship");
        var foreignHost = @"\\NotThisFixtureComputer\root\subscription:" + consumer;
        var foreignNamespace = @"\\.\root\default:" + consumer;
        check(Normalize(foreignHost) != consumer && Normalize(foreignNamespace) != consumer,
            "foreign-host and foreign-namespace bindings cannot borrow a same-named local consumer");

        // Exercise ACL policy as in-memory descriptors. No production backup directory is created or queried.
        var securityType = typeof(AutorunManager).Assembly.GetType("ProcessKeeper.Core.AutorunBackupSecurity")!;
        T Call<T>(string method, params object[] args) => (T)securityType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        void Validate(string method, params object[] args) => securityType.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        bool Denied(string method, params object[] args)
        {
            try { Validate(method, args); return false; }
            catch (TargetInvocationException exception) when (exception.InnerException is IOException) { return true; }
        }
        DirectorySecurity Good() => Call<DirectorySecurity>("NewDirectorySecurity");
        var good = Good();
        Validate("ValidateDescriptor", good, true);
        check(true, "administrator/SYSTEM-only protected backup descriptor passes without filesystem mutation");
        var personal = Good(); personal.SetOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null));
        check(Denied("ValidateDescriptor", personal, true), "personal or untrusted owner cannot supply an elevated recovery backup");
        var writable = Good(); writable.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
        check(Denied("ValidateDescriptor", writable, true), "a backup descriptor with an ordinary-user writer is rejected");
        var inherited = Good(); inherited.SetAccessRuleProtection(false, false);
        check(Denied("ValidateDescriptor", inherited, true), "a backup with inheritable unprotected ACL is rejected");
        var missingSystem = Good(); missingSystem.PurgeAccessRules(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        check(Denied("ValidateDescriptor", missingSystem, true), "a backup ACL missing required SYSTEM access is rejected");
        var weakAncestor = Good(); weakAncestor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
        check(Denied("AssertParentCannotReplaceChildren", weakAncestor), "a user-replaceable ancestor cannot protect administrator recovery files");
        var creatorOnly = Good(); creatorOnly.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.CreateDirectories, AccessControlType.Allow));
        Validate("AssertParentCannotReplaceChildren", creatorOnly);
        check(true, "an administrator-owned ancestor may allow new-child creation without allowing replacement of protected children");
    }
}
