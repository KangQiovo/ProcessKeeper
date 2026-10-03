using System.ComponentModel;
using System.Text.Json;
using Microsoft.UI.Xaml;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;
public sealed partial class RefreshTestApp : Application
{
    public RefreshTestApp() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var checks = new List<object>();
        void Check(bool value, string name) => checks.Add(new { Name = name, Passed = value });
        var app = new AppRow { RowKey = "app:fixture", Name = "Fixture", Summary = "100 MB", IsExpanded = true };
        var changes = Observe(app); app.Notify(); changes.Clear();
        app.Summary = "101 MB"; app.Notify();
        Check(changes.SequenceEqual(new[] { nameof(AppRow.Summary) }), "running memory tick changes summary without rebinding icon, chevron or actions");
        changes.Clear(); for (int i = 0; i < 100; i++) app.Notify();
        Check(changes.Count == 0, "unchanged running ticks produce no binding invalidations");
        app.IsExpanded = false; app.Notify();
        Check(changes.Contains(nameof(AppRow.Chevron)) && !changes.Any(string.IsNullOrEmpty), "expansion updates its chevron through targeted notifications");

        var rule = new RuleRow { RowKey = "rule:fixture", Detail = "100 MB", IsExpanded = true };
        changes = Observe(rule); rule.Notify(); changes.Clear(); rule.Detail = "101 MB"; rule.Notify();
        Check(changes.SequenceEqual(new[] { nameof(RuleRow.Detail) }), "whitelist memory tick changes detail without rebinding icon or expanded state");
        changes.Clear(); rule.Notify(); Check(changes.Count == 0, "unchanged whitelist ticks produce no notifications");
        var installed = new InstalledRow { RowKey = "installed:fixture", Kind = InstalledRowKind.Application, Summary = "100 MB", IsKept = true };
        changes = Observe(installed); installed.Notify(); changes.Clear(); installed.Summary = "101 MB"; installed.Notify();
        Check(changes.SequenceEqual(new[] { nameof(InstalledRow.Summary) }), "installed memory tick leaves cached icon and keep checkbox bindings intact");
        changes.Clear(); L.Language = "en"; installed.Notify(); changes.Clear(); L.Language = "zh-Hans"; installed.Notify();
        Check(changes.Contains(nameof(InstalledRow.KeepText)) && !changes.Contains(nameof(InstalledRow.Icon)) && !changes.Any(string.IsNullOrEmpty), "language change updates translated keep label without resetting icon");
        var autorun = new AutorunRow { Entry = new() { Id = "startup:fixture", Name = "Fixture" }, Summary = "Enabled", ProcessSummary = "1 process", DetailText = "Fixture", IsExpanded = true };
        changes = Observe(autorun); autorun.Notify(); changes.Clear(); autorun.ProcessSummary = "2 processes"; autorun.Notify();
        Check(changes.SequenceEqual(new[] { nameof(AutorunRow.ProcessSummary) }), "startup process tick preserves icon and expansion bindings");
        changes.Clear(); autorun.Notify(); Check(changes.Count == 0, "unchanged startup ticks remain quiet");

        foreach (var name in new[] { "svchost.exe", "explorer.exe", "nvcontainer.exe" })
        {
            var process = ProcessIdentity.Classify(new() { Id = 81, Name = name, Path = @"E:\ThirdParty\" + name, SessionId = 1 });
            Check(!process.IsSystem && process.Category == RunCategory.Background, "third-party executable name does not prove Windows identity: " + name);
        }
        var service = ProcessIdentity.Classify(new() { Id = 82, Name = "thirdparty.exe", Path = @"E:\ThirdParty\thirdparty.exe", SessionId = 0, Services = new[] { new ServiceRecord("VendorService", "Vendor service", 82) } });
        Check(service.Category == RunCategory.Background, "third-party service is not hidden in the system display category");
        Check(new ProtectionPolicy(1, "fixture-user", 123).Evaluate(service, new(DateTimeOffset.Now, new[] { service }, Array.Empty<ApplicationGroup>()), Array.Empty<WhitelistRule>()).Protected,
            "third-party service stays protected from process close independently of display category");
        var unknownCritical = ProcessIdentity.Classify(new() { Id = 83, Name = "vendor.exe", Path = @"E:\ThirdParty\vendor.exe", SessionId = 1, CriticalStatusUnknown = true });
        Check(!unknownCritical.IsSystem && unknownCritical.Category == RunCategory.Background, "unavailable critical query does not claim Windows system ownership");
        Check(new ProtectionPolicy(1, "fixture-user", 123).Evaluate(unknownCritical, new(DateTimeOffset.Now, new[] { unknownCritical }, Array.Empty<ApplicationGroup>()), Array.Empty<WhitelistRule>()).Protected,
            "unavailable critical query remains protected from process close");
        var childRegistration = new UninstallEntry { Name = "Vendor Support Tool", Publisher = "Vendor team", IsSystem = true, CanUninstall = false };
        Check(UninstallDisplay.Classify(childRegistration) == UninstallAppCategory.ThirdPartyApplication, "protected third-party child registration is not a Microsoft system application");
        Check(!childRegistration.CanUninstall && childRegistration.IsSystem, "display category leaves uninstall execution protection unchanged");
        var publisher = new FixturePublisher();
        var componentPath = @"Q:\Windows\System32\svchost.exe";
        publisher.Files[componentPath] = (new PublisherFileIdentity(1, 1, 10, 20, 30), true);
        var components = new SystemComponentClassifier(publisher, @"Q:\Windows");
        Check(components.IsWindowsComponent("svchost.exe", componentPath), "verified Microsoft Windows component receives system display ownership");
        Check(!components.IsWindowsComponent("svchost.exe", @"Q:\Vendor\svchost.exe"), "Windows-name copy outside Windows does not receive system display ownership");
        publisher.Files[componentPath] = (new PublisherFileIdentity(1, 2, 10, 20, 30), false);
        Check(!components.IsWindowsComponent("svchost.exe", componentPath), "file-ID replacement invalidates cached system ownership");
        publisher.Files[componentPath] = (new PublisherFileIdentity(1, 3, 10, 20, 30), true);
        publisher.DuringVerification = () => publisher.Files[componentPath] = (new PublisherFileIdentity(1, 4, 10, 20, 30), true);
        Check(!components.IsWindowsComponent("svchost.exe", componentPath), "replacement during signature verification remains visible");
        publisher.Files.Clear();
        Check(!components.IsWindowsComponent("svchost.exe", componentPath), "missing component identity cannot reuse old system classification");
        var catalog = new ApplicationDisplayCatalog(Array.Empty<GameCatalogEntry>(), Array.Empty<GamePlatformClient>(), new[] { @"Q:\Verified\editor.exe" });
        var editor = new UninstallEntry { Name = "Editor", Publisher = "Unknown registry claim", IconPath = @"Q:\Verified\editor.exe" };
        Check(UninstallDisplay.Classify(editor, catalog) == UninstallAppCategory.SystemApplication, "uninstall system category follows verified Microsoft executable evidence");
        Check(UninstallDisplay.Classify(editor with { Publisher = "Microsoft Corporation", IconPath = @"Q:\Vendor\editor.exe" }, catalog) == UninstallAppCategory.ThirdPartyApplication,
            "Microsoft registry publisher claim cannot classify an unverified third-party executable as system");
        Check(!catalog.IsMicrosoft(editor with { Command = new(editor.IconPath, "", false) }), "verified Microsoft uninstaller alone cannot establish main application ownership");
        Check(catalog.IsMicrosoft(editor with { IconPath = @"Q:\Vendor\uninstall.exe", ApplicationPaths = new[] { editor.IconPath } }), "verified resolved main executable classifies independently of its uninstaller icon");

        var output = Environment.GetEnvironmentVariable("PROCESSKEEPER_REFRESH_TEST_OUTPUT") ?? Path.Combine(Path.GetTempPath(), "processkeeper-refresh-tests.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = checks.All(check => (bool)check.GetType().GetProperty("Passed")!.GetValue(check)!), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
        Exit();
    }
    private static List<string?> Observe(INotifyPropertyChanged row)
    { var changes = new List<string?>(); row.PropertyChanged += (_, args) => changes.Add(args.PropertyName); return changes; }
}
internal sealed class FixturePublisher : IMicrosoftPublisherProbe
{
    internal Dictionary<string, (PublisherFileIdentity Identity, bool Microsoft)> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Action? DuringVerification { get; set; }
    public PublisherFileIdentity? ReadIdentity(string path) => Files.TryGetValue(path, out var file) ? file.Identity : null;
    public bool IsMicrosoft(string path, CancellationToken token)
    { bool result = Files.TryGetValue(path, out var file) && file.Microsoft; DuringVerification?.Invoke(); DuringVerification = null; return result; }
}
