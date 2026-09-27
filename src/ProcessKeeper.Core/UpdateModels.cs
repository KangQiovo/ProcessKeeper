namespace ProcessKeeper.Core;

public enum UpdateSourceMode { Auto, Official, ThirdParty }
public sealed record UpdatePreferences(string Repository = UpdatePolicy.Repository, bool CheckOnStartup = true,
    UpdateSourceMode SourceMode = UpdateSourceMode.Auto, string ThirdPartySourceId = "auto", bool AutoDesktopShortcut = true)
{
    private string _repository = UpdatePolicy.NormalizeRepository(Repository);
    public string Repository { get => _repository; init => _repository = UpdatePolicy.NormalizeRepository(value); }
}
public sealed record UpdateSource(string Id, string Name, string Prefix, bool IsOfficial);
public static class UpdateSources
{
    public static IReadOnlyList<UpdateSource> All { get; } = Array.AsReadOnly(new[]
    {
        new UpdateSource("official", "GitHub", "", true),
        new UpdateSource("ghfast", "ghfast.top", "https://ghfast.top/", false),
        new UpdateSource("ghproxy", "ghproxy.net", "https://ghproxy.net/", false),
        new UpdateSource("gh-proxy", "gh-proxy.com", "https://gh-proxy.com/", false),
        new UpdateSource("llkk", "gh.llkk.cc", "https://gh.llkk.cc/", false),
        new UpdateSource("homeboyc", "ghproxy.homeboyc.cn", "https://ghproxy.homeboyc.cn/", false),
        new UpdateSource("gh-proxy-org", "gh-proxy.org", "https://gh-proxy.org/", false)
    });
}
public sealed record UpdateAsset(long Id, string Name, long Size, string DownloadUrl, string Digest, bool CanAutoInstall, string Restriction);
public sealed record UpdateRelease(string Repository, string Tag, string Version, string Name, DateTimeOffset? PublishedAt,
    string Body, string HtmlUrl, bool Prerelease, IReadOnlyList<UpdateAsset> Assets);
public sealed record UpdateCheckResult(string CurrentVersion, UpdateRelease? LatestRelease, IReadOnlyList<UpdateRelease> Releases,
    bool UpdateAvailable, string Message);
public sealed record UpdateProgress(string Stage, string SourceId, long BytesReceived, long TotalBytes, string Message, long? LatencyMilliseconds = null);
public sealed record UpdateProbeResult(string SourceId, bool Success, long? LatencyMilliseconds, string Message);
/// <summary>A download result is not execution authority. The installer must revalidate its hash and native package before protected staging/execution.</summary>
public sealed record UpdateDownloadResult(string FilePath, string Sha256, string SourceId, UpdateRelease Release, UpdateAsset Asset);
public sealed class UpdateException : Exception
{
    public string Code { get; }
    public DateTimeOffset? RetryAfterUtc { get; }
    public UpdateException(string code, string message, DateTimeOffset? retryAfterUtc = null, Exception? innerException = null)
        : base(message, innerException) { Code = code; RetryAfterUtc = retryAfterUtc; }
}
