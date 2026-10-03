using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ProcessKeeper.Core;

/// <summary>One frozen official asset and two bounded owned partial files. Pause retains bytes; explicit cancel discards them.</summary>
public sealed class UpdateDownloadSession
{
    internal UpdateRelease Release { get; }
    internal UpdateAsset Asset { get; }
    internal string DirectoryPath { get; }
    internal string PartialPath { get; }
    internal string RestartPath { get; }
    internal string CompletedPath { get; }
    internal string ActivePath { get; set; }
    internal readonly Dictionary<string, (long Length, string Hash)> Receipts = new(StringComparer.OrdinalIgnoreCase);
    internal int Running, Discarded;
    internal UpdateDownloadResult? Result;
    private long _bytes;
    public long BytesReceived => Interlocked.Read(ref _bytes);
    public long TotalBytes => Asset.Size;
    public string SourceId { get; internal set; } = "";
    public bool IsComplete => Result is not null;
    internal void SetBytes(long value) => Interlocked.Exchange(ref _bytes, value);
    internal UpdateDownloadSession(UpdateRelease release, UpdateAsset asset, string directory)
    {
        Release = release; Asset = asset; DirectoryPath = directory;
        var stem = Path.Combine(directory, "ProcessKeeper-" + Guid.NewGuid().ToString("N"));
        PartialPath = stem + ".part"; RestartPath = stem + ".restart"; CompletedPath = stem + ".exe"; ActivePath = PartialPath;
    }
}

public sealed partial class UpdateService
{
    public UpdateDownloadSession CreateDownloadSession(UpdateRelease release, UpdateAsset asset, string stagingDirectory)
    {
        ValidateDirectory(stagingDirectory);
        if (release.Repository != UpdatePolicy.Repository || !release.Assets.Any(candidate => candidate == asset) || !asset.CanAutoInstall ||
            asset.Restriction.Length > 0 || !ValidDigest(asset.Digest) || asset.Size <= 0 || asset.Size > MaximumDownloadBytes ||
            asset.PackageTarget != UpdatePackagePolicy.Identify(asset.Name, release.Version) || !UpdatePackagePolicy.Supports(asset.PackageTarget, _runtime))
            throw Error("unsupported-asset", "所选更新文件不能在当前环境中安装。");
        return new(release, asset, Path.GetFullPath(stagingDirectory));
    }

    public void DiscardDownloadSession(UpdateDownloadSession session)
    {
        if (Interlocked.CompareExchange(ref session.Running, 1, 0) != 0) throw new IOException("An active update download cannot be discarded.");
        try
        {
            ValidateDirectory(session.DirectoryPath);
            foreach (var path in new[] { session.PartialPath, session.RestartPath, session.CompletedPath })
            {
                if (!File.Exists(path)) continue;
                using (var file = _openResumeFile(path))
                {
                    if (path != session.CompletedPath) RequireReceipt(session, path, file);
                    else UpdateTrustedFiles.RequireHash(file, session.Asset.Digest.Substring(7));
                }
                File.Delete(path);
            }
            session.Receipts.Clear(); session.Result = null; session.SetBytes(0); session.Discarded = 1;
        }
        finally { Interlocked.Exchange(ref session.Running, 0); }
    }

    public async Task<UpdateDownloadResult> DownloadResumableAsync(UpdatePreferences preferences, UpdateDownloadSession session,
        IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        preferences = UpdatePreferencesStore.Validate(preferences);
        if (session.Discarded != 0) throw new IOException("This update download was discarded.");
        ValidateDirectory(session.DirectoryPath);
        await Enter(cancellationToken).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref session.Running, 1, 0) != 0) { Operations.Release(); throw new IOException("This update download is already running."); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            if (session.Discarded != 0) throw new IOException("This update download was discarded.");
            var verified = await Revalidate(preferences, session.Release, session.Asset, timeout.Token).ConfigureAwait(false);
            if (session.Result is not null)
            {
                using var completed = _openResumeFile(session.CompletedPath);
                UpdateTrustedFiles.RequireHash(completed, verified.Asset.Digest.Substring(7));
                return session.Result;
            }
            if (File.Exists(session.ActivePath))
            {
                using var retained = _openResumeFile(session.ActivePath);
                await Task.Run(() => RequireReceipt(session, session.ActivePath, retained), timeout.Token).ConfigureAwait(false);
                session.SetBytes(retained.Length);
            }
            Report(progress, "resuming", session.SourceId, session.BytesReceived, session.TotalBytes, L.T("继续下载已验证身份的更新文件。"));
            var probes = await ProbeSources(preferences, verified.Asset, progress, timeout.Token).ConfigureAwait(false);
            var available = probes.Where(probe => probe.Success).OrderBy(probe => probe.LatencyMilliseconds).ToArray();
            if (available.Length == 0) throw Error("no-source", "所有候选下载源均不可用。请查看各源检测结果。");
            Exception? last = null;
            foreach (var probe in available)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var source = UpdateSources.All.Single(candidate => candidate.Id == probe.SourceId);
                session.SourceId = source.Id;
                try { return await ResumeSource(session, source, verified.Release, verified.Asset, progress, timeout.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is UpdateException or HttpRequestException or IOException)
                {
                    timeout.Token.ThrowIfCancellationRequested(); last = error;
                    if (error is UpdateException { Code: "digest" or "executable" })
                    {
                        if (File.Exists(session.ActivePath))
                        {
                            using (var corrupt = _openResumeFile(session.ActivePath)) RequireReceipt(session, session.ActivePath, corrupt);
                            File.Delete(session.ActivePath); session.Receipts.Remove(session.ActivePath);
                        }
                        var retained = session.ActivePath == session.PartialPath ? session.RestartPath : session.PartialPath;
                        session.ActivePath = retained; session.SetBytes(session.Receipts.TryGetValue(retained, out var receipt) ? receipt.Length : 0);
                    }
                    Report(progress, "source-failed", source.Id, session.BytesReceived, session.TotalBytes, source.Name + " | " + error.Message);
                }
            }
            throw last ?? Error("download", "更新下载失败。");
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            Report(progress, "paused", session.SourceId, session.BytesReceived, session.TotalBytes, L.T("更新下载已暂停，已下载片段已保留。"));
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested) { throw Error("timeout", "更新请求超时，请稍后重试。"); }
        finally { Interlocked.Exchange(ref session.Running, 0); Operations.Release(); }
    }

    private async Task<UpdateDownloadResult> ResumeSource(UpdateDownloadSession session, UpdateSource source,
        UpdateRelease release, UpdateAsset asset, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        long offset = File.Exists(session.ActivePath) ? session.BytesReceived : 0;
        using var response = offset == asset.Size ? null : await OpenResumeResponse(source, asset, offset, token).ConfigureAwait(false);
        if (response is not null && offset > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            var next = session.ActivePath == session.PartialPath ? session.RestartPath : session.PartialPath;
            if (File.Exists(next))
            {
                using (var old = _openResumeFile(next)) RequireReceipt(session, next, old);
                File.Delete(next); session.Receipts.Remove(next);
            }
            // Keep the previous active file until a valid replacement is complete.
            session.ActivePath = next; offset = 0; session.SetBytes(0);
            Report(progress, "restarting", source.Id, 0, asset.Size, L.T("此下载源不支持续传，正在重新下载；原片段暂时保留。"));
        }
        var path = session.ActivePath; bool created = !File.Exists(path);
        using var file = created ? _createDownloadFile(path) : _openResumeFile(path);
        if (!created) RequireReceipt(session, path, file);
        if (file.Length != offset || file.Length > asset.Size) throw Error("partial-changed", "更新暂存片段已变化，请取消后重新下载。");
        try
        {
            file.Position = offset;
            if (response is not null)
            {
                using var cancel = token.Register(() => response.Dispose());
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[65536]; var report = Stopwatch.StartNew();
                Report(progress, "downloading", source.Id, offset, asset.Size, L.F($"正在通过 {source.Name} 下载更新。"));
                while (true)
                {
                    token.ThrowIfCancellationRequested(); int read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); if (read == 0) break;
                    if (file.Position + read > asset.Size) throw Error("size", "更新文件大小与官方记录不一致。");
                    await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false); session.SetBytes(file.Position);
                    if (report.ElapsedMilliseconds >= 150) { Report(progress, "downloading", source.Id, file.Position, asset.Size, L.F($"正在通过 {source.Name} 下载更新。")); report.Restart(); }
                }
            }
            token.ThrowIfCancellationRequested(); file.Flush(true);
            if (file.Length != asset.Size) throw Error("size", "更新文件大小与官方记录不一致。");
            Report(progress, "verifying", source.Id, file.Length, asset.Size, L.T("正在核对官方 SHA-256 摘要与 EXE 文件格式。"));
            var hash = await Task.Run(() => HashResume(file), token).ConfigureAwait(false);
            if (hash != asset.Digest.Substring(7)) throw Error("digest", "更新文件 SHA-256 与 GitHub 官方摘要不一致，已拒绝此文件。");
            ValidatePortableHeader(file); token.ThrowIfCancellationRequested();
            session.Receipts[path] = (file.Length, hash);
            file.Dispose(); File.Move(path, session.CompletedPath); session.Receipts.Remove(path);
            session.Result = new(session.CompletedPath, hash, source.Id, release, asset); session.SetBytes(asset.Size);
            foreach (var retained in new[] { session.PartialPath, session.RestartPath }.Where(candidate => candidate != path && File.Exists(candidate)))
            {
                using (var old = _openResumeFile(retained)) RequireReceipt(session, retained, old);
                File.Delete(retained); session.Receipts.Remove(retained);
            }
            Report(progress, "completed", source.Id, asset.Size, asset.Size, L.T("更新下载及官方摘要校验完成；安装前仍需验证通用启动器。"));
            return session.Result;
        }
        finally
        {
            if (session.Result is null)
            {
                try { file.Flush(true); session.SetBytes(file.Length); session.Receipts[path] = (file.Length, HashResume(file)); }
                catch (ObjectDisposedException) { /* A completed file was moved; no partial receipt is created. */ }
            }
        }
    }

    private async Task<HttpResponseMessage> OpenResumeResponse(UpdateSource source, UpdateAsset asset, long offset, CancellationToken token)
    {
        var uri = new Uri(source.IsOfficial ? asset.DownloadUrl : source.Prefix + asset.DownloadUrl);
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            RequireDownloadUri(uri, !source.IsOfficial);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.AcceptEncoding.ParseAdd("identity"); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, asset.Size - 1);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                ValidateEffectiveUri(response, uri, false, !source.IsOfficial);
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 5 || response.Headers.Location is null) throw Error("redirect", "更新下载重定向次数过多或缺少目标地址。");
                    uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location); RequireDownloadUri(uri, !source.IsOfficial); response.Dispose(); continue;
                }
                if (!response.IsSuccessStatusCode) ThrowStatus(response, false);
                if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                    response.Content.Headers.ContentType?.MediaType?.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0) throw Error("html", "下载源返回了网页，而不是更新文件。");
                long expected;
                if (response.StatusCode == HttpStatusCode.PartialContent && offset > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is null || range.From != offset || range.To != asset.Size - 1 || range.Length != asset.Size) throw Error("range", "下载源返回的片段范围与官方文件大小不一致。");
                    expected = asset.Size - offset;
                }
                else if (response.StatusCode == HttpStatusCode.OK) expected = asset.Size;
                else throw Error("partial", "更新下载未返回完整文件。");
                if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != expected) throw Error("size", "更新文件大小与官方记录不一致。");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw Error("redirect", "更新下载重定向次数过多或缺少目标地址。");
    }
    private static string HashResume(FileStream file)
    {
        using var hash = SHA256.Create(); long position = file.Position; file.Position = 0;
        string value = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); file.Position = position; return value;
    }
    private static void RequireReceipt(UpdateDownloadSession session, string path, FileStream file)
    {
        if (!session.Receipts.TryGetValue(path, out var receipt) || file.Length != receipt.Length || HashResume(file) != receipt.Hash)
            throw Error("partial-changed", "更新暂存片段已变化，请取消后重新下载。");
    }
}
