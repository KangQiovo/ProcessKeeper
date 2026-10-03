using ProcessKeeper.Core;

namespace ProcessKeeper.App;

/// <summary>UI effect boundary. Production delegates retain Core verification; fixtures replace every effect.</summary>
public sealed class LegacyUpdateBackend
{
    public Func<UpdatePreferences, string, CancellationToken, Task<UpdateCheckResult>> Check { get; set; } = async (preferences, version, token) =>
    { using var service = new UpdateService(); return await service.CheckAsync(preferences, version, token); };
    public Func<UpdatePreferences, UpdateRelease, UpdateAsset, IProgress<UpdateProgress>?, CancellationToken, Task<IReadOnlyList<UpdateProbeResult>>> Probe { get; set; } = async (preferences, release, asset, progress, token) =>
    { using var service = new UpdateService(); return await service.ProbeAsync(preferences, release, asset, progress, token); };
    private Func<UpdatePreferences, UpdateRelease, UpdateAsset, string, IProgress<UpdateProgress>?, CancellationToken, Task<UpdateDownloadResult>> _download = async (preferences, release, asset, stage, progress, token) =>
    { using var service = new UpdateService(); return await service.DownloadAsync(preferences, release, asset, stage, progress, token); };
    public Func<UpdatePreferences, UpdateRelease, UpdateAsset, string, IProgress<UpdateProgress>?, CancellationToken, Task<UpdateDownloadResult>> Download
    {
        get => _download;
        set { _download = value; CreateSession = null; ResumeDownload = null; DiscardSession = null; }
    }
    public Func<UpdateRelease, UpdateAsset, string, UpdateDownloadSession>? CreateSession { get; set; } = (release, asset, stage) =>
    { using var service = new UpdateService(); return service.CreateDownloadSession(release, asset, stage); };
    public Func<UpdatePreferences, UpdateDownloadSession, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>>? ResumeDownload { get; set; } = async (preferences, session, progress, token) =>
    { using var service = new UpdateService(); return await service.DownloadResumableAsync(preferences, session, progress, token); };
    public Action<UpdateDownloadSession>? DiscardSession { get; set; } = session =>
    { using var service = new UpdateService(); service.DiscardDownloadSession(session); };
    public Func<bool> HasTrustedLauncher { get; set; } = () => LauncherContextReader.TryGetCurrent(out _, out _);
    public Func<string> CreateStage { get; set; } = () => UpdateInstaller.CreateDownloadStage(CurrentContext());
    public Action<string> CleanupStage { get; set; } = stage => UpdateInstaller.DiscardDownloadStage(CurrentContext(), stage);
    public Func<UpdateDownloadResult, CancellationToken, Task<LegacyUpdateTransaction>> Prepare { get; set; } = (download, token) => Task.Run(() =>
    {
        var prepared = UpdateInstaller.Prepare(CurrentContext(), download, token);
        return new LegacyUpdateTransaction(() => UpdateInstaller.Start(prepared), () => UpdateInstaller.DiscardPrepared(prepared));
    }, token);
    public Func<Task<DesktopShortcutResult>> CreateShortcut { get; set; } = () => Task.Run(() => DesktopShortcutService.Ensure(CurrentContext()));
    public Action? ExitAfterUpdate { get; set; }
    public bool ActivateProgressWindow { get; set; } = true;
    private static LauncherContext CurrentContext()
    {
        if (LauncherContextReader.TryGetCurrent(out var context, out var error)) return context!;
        throw new InvalidOperationException(error);
    }
}

public sealed record LegacyUpdateTransaction(Func<UpdateStartResult> Start, Action? Discard = null);
