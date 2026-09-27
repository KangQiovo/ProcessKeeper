using ProcessKeeper.Core;
using System.Diagnostics;
using System.Security.Principal;

namespace ProcessKeeper.App;

/// <summary>External effects are replaceable by an isolated fixture. UI never implements process termination.</summary>
public sealed class LegacyBackend
{
    public LegacyUpdateBackend Updates { get; set; } = new();
    public Func<ProcessSnapshot> Capture { get; set; } = new ProcessCollector().Capture;
    public Func<CancellationToken, IReadOnlyList<InstalledApplication>> ScanInstalled { get; set; } = new InstalledApplicationCatalog().Scan;
    public Func<CancellationToken, AutorunSnapshot> ScanAutoruns { get; set; } = new AutorunCatalog().Scan;
    public Func<ProcessSnapshot, IReadOnlyList<InstalledApplication>, IReadOnlyList<AutorunEntry>, CancellationToken, ApplicationDisplayCatalog> CaptureDisplay { get; set; } = new ApplicationDisplayService().Capture;
    public Func<AutorunEntry, bool, CancellationToken, AutorunChangeResult> ChangeAutorun { get; set; } = new AutorunManager().ChangeEnabled;
    public Func<string, string, Task<bool>>? Confirm { get; set; }
    public Action<string, string>? ShowInformation { get; set; }
    public Action<string>? OpenLocation { get; set; }
    public Action<string>? OpenWebPage { get; set; }
    public Func<string, string?>? SelectExportPath { get; set; }
    public Func<ProcessRecord, ProcessSnapshot, Func<ProcessRecord, ProtectionDecision>, CancellationToken, Task<CloseBatchReport>>? ExecuteSensitiveClose { get; set; }
    public Func<IReadOnlyList<ProcessRecord>, bool, Func<ProcessRecord, ProtectionDecision>, ProcessSnapshot, CancellationToken, Task<CloseBatchReport>>? ExecuteClose { get; set; }
    public Func<IReadOnlyList<ProcessRecord>, Func<ProcessRecord, ProtectionDecision>, CancellationToken, Task<IReadOnlyList<CloseResult>>>? ExecuteSteamForce { get; set; }
    public Func<RuntimeEnvironmentReport> CaptureEnvironment { get; set; } = RuntimeEnvironmentProbe.Capture;
    public Func<System.Windows.Rect> WorkingArea { get; set; } = () => System.Windows.SystemParameters.WorkArea;
    public bool? AdministratorOverride { get; set; }
    public bool IsAdministrator { get { if (AdministratorOverride.HasValue) return AdministratorOverride.Value; using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
    public ProtectionPolicy Policy { get; }
    public LegacyBackend() { using var identity = WindowsIdentity.GetCurrent(); using var process = Process.GetCurrentProcess(); Policy = new ProtectionPolicy(process.SessionId, identity.User?.Value ?? "", process.Id); }
}
