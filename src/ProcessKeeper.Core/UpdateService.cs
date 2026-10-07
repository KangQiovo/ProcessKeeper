using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProcessKeeper.Core;

/// <summary>Official metadata authority with untrusted, checksum-verified download transports. This class never launches an updater.</summary>
public sealed partial class UpdateService : IDisposable
{
    public const long MaximumDownloadBytes = 512L * 1024 * 1024;
    private const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private static readonly SemaphoreSlim Operations = new(1, 1);
    private readonly HttpClient _http;
    private readonly Func<string, FileStream> _createDownloadFile;
    private readonly Func<string, FileStream> _openResumeFile;
    private readonly UpdateRuntimeIdentity _runtime;
    private bool _disposed;
    public UpdateService(HttpMessageHandler? handler = null, Func<string, FileStream>? createDownloadFile = null,
        Func<string, FileStream>? openResumeFile = null, UpdateRuntimeIdentity? runtime = null)
    {
        handler ??= new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false };
        _http = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProcessKeeper/1.7.2");
        _createDownloadFile = createDownloadFile ?? UpdateTrustedFiles.Create;
        _openResumeFile = openResumeFile ?? UpdateTrustedFiles.OpenResume;
        _runtime = runtime ?? UpdatePackagePolicy.Current();
    }
    public void Dispose() { _disposed = true; _http.Dispose(); }
    public async Task<UpdateCheckResult> CheckAsync(UpdatePreferences preferences, string currentVersion, CancellationToken cancellationToken = default)
    {
        preferences = UpdatePreferencesStore.Validate(preferences);
        if (!UpdateVersion.TryParse(currentVersion, out var current)) throw Error("version", "当前应用版本不是有效的 SemVer，无法比较更新。");
        await Enter(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var json = await OfficialJson("https://api.github.com/repos/" + UpdatePolicy.Repository + "/releases?per_page=20", timeout.Token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw Error("metadata", "GitHub 发布信息格式无效。");
            var releases = new List<UpdateRelease>();
            foreach (var item in json.RootElement.EnumerateArray().Take(20))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.Object || True(item, "draft")) continue;
                releases.Add(ParseRelease(item));
            }
            UpdateRelease? latest = null; UpdateVersion? latestVersion = null;
            foreach (var release in releases)
                if (UpdateVersion.TryParse(release.Tag, out var version) && (latestVersion is null || version!.CompareTo(latestVersion) > 0)) { latest = release; latestVersion = version; }
            var available = latestVersion is not null && latestVersion.CompareTo(current) > 0;
            return new UpdateCheckResult(current!.Value, latest, releases.AsReadOnly(), available,
                releases.Count == 0 ? L.T("此仓库尚未发布可用版本。") : latest is null ? L.T("发布标签不是有效的 SemVer，无法判断是否有更新。") : available ? L.F($"发现新版本：{latest.Version}") : L.T("当前未发现更新版本。"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (Exception) when (timeout.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (HttpRequestException ex) { throw new UpdateException("network", L.T("无法连接更新服务。") + " " + ex.Message, null, ex); }
        catch (JsonException ex) { throw new UpdateException("metadata", L.T("GitHub 发布信息格式无效。"), null, ex); }
        finally { Operations.Release(); }
    }
    public async Task<IReadOnlyList<UpdateProbeResult>> ProbeAsync(UpdatePreferences preferences, UpdateRelease release, UpdateAsset asset,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        preferences = UpdatePreferencesStore.Validate(preferences); await Enter(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var verified = await Revalidate(preferences, release, asset, timeout.Token).ConfigureAwait(false);
            return await ProbeSources(preferences, verified.Asset, progress, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (Exception) when (timeout.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (HttpRequestException ex) { throw new UpdateException("network", L.T("无法连接更新服务。") + " " + ex.Message, null, ex); }
        catch (JsonException ex) { throw new UpdateException("metadata", L.T("GitHub 发布信息格式无效。"), null, ex); }
        finally { Operations.Release(); }
    }
    public async Task<UpdateDownloadResult> DownloadAsync(UpdatePreferences preferences, UpdateRelease release, UpdateAsset asset, string stagingDirectory,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        preferences = UpdatePreferencesStore.Validate(preferences); ValidateDirectory(stagingDirectory);
        await Enter(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            Report(progress, "metadata", "official", 0, asset.Size, L.T("正在读取 GitHub 官方发布信息。"));
            var verified = await Revalidate(preferences, release, asset, timeout.Token).ConfigureAwait(false);
            UpdateException? last = null;
            foreach (var plan in DownloadPlans(preferences))
            {
                var probes = await ProbeSources(plan, verified.Asset, progress, timeout.Token).ConfigureAwait(false);
                foreach (var probe in probes.Where(p => p.Success).OrderBy(p => p.LatencyMilliseconds))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var source = UpdateSources.All.Single(s => s.Id == probe.SourceId);
                    try { return await DownloadSource(source, verified.Release, verified.Asset, stagingDirectory, progress, timeout.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException)
                    {
                        if (timeout.IsCancellationRequested) throw new OperationCanceledException(timeout.Token);
                        last = ex as UpdateException ?? new UpdateException("download", L.T("更新下载失败。") + " " + ex.Message, null, ex);
                        Report(progress, "source-failed", source.Id, 0, verified.Asset.Size, source.Name + " | " + last.Message);
                    }
                }
            }
            throw last ?? Error("no-source", "所有候选下载源均不可用。请查看各源检测结果。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (Exception) when (timeout.IsCancellationRequested) { cancellationToken.ThrowIfCancellationRequested(); throw Error("timeout", "更新请求超时，请稍后重试。"); }
        catch (HttpRequestException ex) { throw new UpdateException("network", L.T("无法连接更新服务。") + " " + ex.Message, null, ex); }
        catch (JsonException ex) { throw new UpdateException("metadata", L.T("GitHub 发布信息格式无效。"), null, ex); }
        finally { Operations.Release(); }
    }
    private async Task Enter(CancellationToken token)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(UpdateService));
        await Operations.WaitAsync(token).ConfigureAwait(false);
        if (_disposed) { Operations.Release(); throw new ObjectDisposedException(nameof(UpdateService)); }
    }
    private async Task<(UpdateRelease Release, UpdateAsset Asset)> Revalidate(UpdatePreferences preferences, UpdateRelease expectedRelease, UpdateAsset expectedAsset, CancellationToken token)
    {
        UpdatePreferencesStore.Validate(preferences);
        if (!string.Equals(UpdatePolicy.Repository, expectedRelease.Repository, StringComparison.OrdinalIgnoreCase) ||
            !UpdateVersion.TryParse(expectedRelease.Tag, out _)) throw Error("changed", "所选发布信息与当前仓库不一致，请重新检查更新。");
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token); requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var json = await OfficialJson("https://api.github.com/repos/" + UpdatePolicy.Repository + "/releases/tags/" + Uri.EscapeDataString(expectedRelease.Tag), requestTimeout.Token).ConfigureAwait(false);
        if (json.RootElement.ValueKind != JsonValueKind.Object || True(json.RootElement, "draft")) throw Error("changed", "所选发布信息与当前仓库不一致，请重新检查更新。");
        var release = ParseRelease(json.RootElement);
        var asset = release.Assets.SingleOrDefault(a => a.Id == expectedAsset.Id);
        if (release.Tag != expectedRelease.Tag || release.Version != expectedRelease.Version || asset is null ||
            asset.Name != expectedAsset.Name || asset.Size != expectedAsset.Size || asset.DownloadUrl != expectedAsset.DownloadUrl || asset.Digest != expectedAsset.Digest)
            throw Error("changed", "更新文件信息已变化，请重新检查更新。");
        if (!asset.CanAutoInstall) throw new UpdateException("unsupported-asset", asset.Restriction);
        return (release, asset);
    }
    private async Task<JsonDocument> OfficialJson(string url, CancellationToken token)
    {
        var uri = new Uri(url); if (uri.Scheme != "https" || uri.Host != "api.github.com" || uri.Port != 443 || uri.UserInfo.Length != 0) throw Error("metadata", "发布信息必须来自 GitHub 官方接口。");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json")); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        ValidateEffectiveUri(response, uri, true, false);
        if (!response.IsSuccessStatusCode) ThrowStatus(response, true);
        if (response.Content.Headers.ContentLength > MaximumMetadataBytes) throw Error("metadata-size", "GitHub 发布信息超过读取上限。");
        using var cancel = token.Register(() => response.Dispose());
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[16384];
        while (true)
        {
            token.ThrowIfCancellationRequested(); var read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); if (read == 0) break;
            if (memory.Length + read > MaximumMetadataBytes) throw Error("metadata-size", "GitHub 发布信息超过读取上限。");
            memory.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested(); return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
    }
    private static void ThrowStatus(HttpResponseMessage response, bool metadata)
    {
        var status = (int)response.StatusCode;
        if (status == 429 || status == 403 && (Header(response, "X-RateLimit-Remaining") == "0" || Header(response, "Retry-After").Length > 0))
        {
            DateTimeOffset? retry = null;
            if (long.TryParse(Header(response, "X-RateLimit-Reset"), out var seconds) && seconds > 0 && seconds < 253402300799)
                retry = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var after = Header(response, "Retry-After");
            if (int.TryParse(after, out var delay) && delay >= 0 && delay <= 86400) retry = DateTimeOffset.UtcNow.AddSeconds(delay);
            else if (DateTimeOffset.TryParse(after, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) retry = date.ToUniversalTime();
            throw new UpdateException("rate-limit", L.T("GitHub 请求次数已达限制，请在限制重置后重试。"), retry);
        }
        if (metadata && status == 404) throw Error("not-found", "未找到公开仓库或发布版本，请检查仓库设置。");
        if (metadata && status >= 300 && status < 400) throw Error("redirect", "GitHub 仓库地址发生变化，请使用当前仓库地址。");
        throw new UpdateException("http-" + status.ToString(CultureInfo.InvariantCulture), L.F($"更新服务返回 HTTP {status}。"));
    }
    private static string Header(HttpResponseMessage response, string key) => response.Headers.TryGetValues(key, out var values) ? values.FirstOrDefault() ?? "" : "";
    private static bool True(JsonElement value, string name) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement value, string name, int maximum = 4096) => value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? Limit(p.GetString() ?? "", maximum) : "";
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value.Substring(0, maximum);
    private static UpdateException Error(string code, string source) => new(code, L.T(source));
    private static void Report(IProgress<UpdateProgress>? progress, string stage, string source, long received, long total, string message, long? latency = null) => progress?.Report(new UpdateProgress(stage, source, received, total, message, latency));
}
