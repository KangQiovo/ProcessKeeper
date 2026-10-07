namespace ProcessKeeper.Core;

internal sealed record InstalledPackageSeed(string Name, string Publisher, string FamilyName, string FullName,
    string InstallLocation, IReadOnlyList<string> ExecutablePaths, string Warning, string ExternalInstallLocation = "");
internal static class InstalledPackageCatalog
{
    internal static bool IsMicrosoftExecutable(string path, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return false; }
    internal static IReadOnlyList<InstalledPackageSeed> Read(ICollection<string> warnings, CancellationToken token = default, Func<bool>? budgetAvailable = null)
    { token.ThrowIfCancellationRequested(); warnings.Add(L.T("此兼容版本仅扫描传统桌面软件，不读取打包应用。")); return []; }
}

public sealed class ChromiumLiveService
{
    public ChromiumLiveService(IChromiumLiveHost? host = null) { }
    public Task<ChromiumLiveDiscoveryResult> DiscoverAsync(IReadOnlyList<ProcessRecord> scope, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(new ChromiumLiveDiscoveryResult([], [L.T("此兼容版本不支持浏览器实时页面预览；可尝试打开已有窗口。") ])); }
    public Task<ChromiumLiveSession> ConnectAsync(BrowserPageTarget target, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); throw new PlatformNotSupportedException(L.T("此兼容版本不支持浏览器实时页面预览；可尝试打开已有窗口。")); }
}
public sealed class ChromiumLiveSession : IAsyncDisposable
{
    internal ChromiumLiveSession() { }
    public Task<byte[]> CaptureFrameAsync(CancellationToken cancellationToken = default) => throw new PlatformNotSupportedException(L.T("此兼容版本不支持浏览器实时页面预览；可尝试打开已有窗口。"));
    public ValueTask DisposeAsync() => default;
}
