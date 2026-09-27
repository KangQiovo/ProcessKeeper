using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ProcessKeeper.Core;

public sealed partial class UpdateService
{
    private static IEnumerable<UpdateSource> Candidates(UpdatePreferences preferences) => UpdateSources.All.Where(source => preferences.SourceMode switch
    {
        UpdateSourceMode.Official => source.IsOfficial,
        UpdateSourceMode.ThirdParty => !source.IsOfficial && (preferences.ThirdPartySourceId == "auto" || preferences.ThirdPartySourceId == source.Id),
        _ => true
    });
    private async Task<IReadOnlyList<UpdateProbeResult>> ProbeSources(UpdatePreferences preferences, UpdateAsset asset, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        // A source's latency starts after its slot is acquired, so queueing is not
        // mistaken for network latency. At most three response streams are alive.
        using var slots = new SemaphoreSlim(3, 3);
        var tasks = Candidates(preferences).Select(async source =>
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try { return await Probe(source, asset, progress, token).ConfigureAwait(false); }
            finally { slots.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    private async Task<UpdateProbeResult> Probe(UpdateSource source, UpdateAsset asset, IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        Report(progress, "probing", source.Id, 0, asset.Size, L.F($"正在检测下载源：{source.Name}"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var watch = Stopwatch.StartNew();
        try
        {
            var expected = (int)Math.Min(4096, asset.Size);
            using var response = await OpenDownload(source, asset, expected, timeout.Token).ConfigureAwait(false);
            using var cancel = timeout.Token.Register(() => response.Dispose());
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var buffer = new byte[expected]; var offset = 0;
            while (offset < expected)
            {
                timeout.Token.ThrowIfCancellationRequested(); var read = await stream.ReadAsync(buffer, offset, expected - offset, timeout.Token).ConfigureAwait(false);
                if (read == 0) throw Error("short-response", "下载源返回的文件片段不完整。"); offset += read;
            }
            if (offset < 2 || buffer[0] != 'M' || buffer[1] != 'Z') throw Error("not-executable", "下载源返回的内容不是 Windows EXE 文件。");
            watch.Stop(); Report(progress, "probe-success", source.Id, offset, asset.Size, L.F($"下载源 {source.Name} 延迟 {watch.ElapsedMilliseconds} 毫秒。"), watch.ElapsedMilliseconds);
            return new UpdateProbeResult(source.Id, true, watch.ElapsedMilliseconds, L.T("下载片段检测成功。"));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UpdateException or OperationCanceledException or ObjectDisposedException)
        {
            token.ThrowIfCancellationRequested();
            var message = timeout.IsCancellationRequested ? L.T("下载源检测超时。") : ex.Message;
            Report(progress, "source-failed", source.Id, 0, asset.Size, source.Name + " | " + message);
            return new UpdateProbeResult(source.Id, false, null, message);
        }
    }
    private async Task<HttpResponseMessage> OpenDownload(UpdateSource source, UpdateAsset asset, int rangeBytes, CancellationToken token)
    {
        var uri = new Uri(source.IsOfficial ? asset.DownloadUrl : source.Prefix + asset.DownloadUrl);
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            RequireDownloadUri(uri, !source.IsOfficial);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (rangeBytes > 0) request.Headers.Range = new RangeHeaderValue(0, rangeBytes - 1);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            try
            {
                ValidateEffectiveUri(response, uri, false, !source.IsOfficial);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects == 5 || response.Headers.Location is null) throw Error("redirect", "更新下载重定向次数过多或缺少目标地址。");
                    uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                    RequireDownloadUri(uri, !source.IsOfficial); response.Dispose(); continue;
                }
                if (!response.IsSuccessStatusCode) ThrowStatus(response, false);
                if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))) throw Error("encoding", "更新下载返回了不支持的内容编码。");
                if (response.Content.Headers.ContentType?.MediaType?.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0) throw Error("html", "下载源返回了网页，而不是更新文件。");
                if (rangeBytes == 0)
                {
                    if (response.StatusCode != HttpStatusCode.OK) throw Error("partial", "更新下载未返回完整文件。");
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != asset.Size) throw Error("size", "更新文件大小与官方记录不一致。");
                }
                else if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is null || range.From != 0 || range.To != rangeBytes - 1 || range.Length != asset.Size) throw Error("range", "下载源返回的片段范围与官方文件大小不一致。");
                }
                else if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != asset.Size)
                    throw Error("size", "更新文件大小与官方记录不一致。");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw Error("redirect", "更新下载重定向次数过多或缺少目标地址。");
    }
    private async Task<UpdateDownloadResult> DownloadSource(UpdateSource source, UpdateRelease release, UpdateAsset asset, string directory,
        IProgress<UpdateProgress>? progress, CancellationToken token)
    {
        ValidateDirectory(directory);
        var stem = Path.Combine(Path.GetFullPath(directory), "ProcessKeeper-" + Guid.NewGuid().ToString("N")); var partial = stem + ".part"; var complete = stem + ".exe";
        var completed = false;
        try
        {
            using var response = await OpenDownload(source, asset, 0, token).ConfigureAwait(false);
            using var cancel = token.Register(() => response.Dispose());
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            string actual;
            using (var file = _createDownloadFile(partial))
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536]; long received = 0; var lastReport = Stopwatch.StartNew();
                Report(progress, "downloading", source.Id, 0, asset.Size, L.F($"正在通过 {source.Name} 下载更新。"));
                while (true)
                {
                    token.ThrowIfCancellationRequested(); var read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); if (read == 0) break;
                    if (received + read > asset.Size || received + read > MaximumDownloadBytes) throw Error("size", "更新文件大小与官方记录不一致。");
                    hash.TransformBlock(buffer, 0, read, buffer, 0); await file.WriteAsync(buffer, 0, read, token).ConfigureAwait(false); received += read;
                    if (lastReport.ElapsedMilliseconds >= 150) { Report(progress, "downloading", source.Id, received, asset.Size, L.F($"正在通过 {source.Name} 下载更新。")); lastReport.Restart(); }
                }
                token.ThrowIfCancellationRequested(); if (received != asset.Size) throw Error("size", "更新文件大小与官方记录不一致。");
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); actual = BitConverter.ToString(hash.Hash!).Replace("-", "").ToLowerInvariant();
                Report(progress, "verifying", source.Id, received, asset.Size, L.T("正在核对官方 SHA-256 摘要与 EXE 文件格式。"));
                if (!actual.Equals(asset.Digest.Substring(7), StringComparison.Ordinal)) throw Error("digest", "更新文件 SHA-256 与 GitHub 官方摘要不一致，已拒绝此文件。");
                file.Flush(true); ValidatePortableHeader(file); token.ThrowIfCancellationRequested();
            }
            File.Move(partial, complete); completed = true;
            Report(progress, "completed", source.Id, asset.Size, asset.Size, L.T("更新下载及官方摘要校验完成；安装前仍需验证通用启动器。"));
            return new UpdateDownloadResult(complete, actual, source.Id, release, asset);
        }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
        finally { if (File.Exists(partial)) File.Delete(partial); if (!completed && File.Exists(complete)) File.Delete(complete); }
    }
    private static void ValidatePortableHeader(FileStream stream)
    {
        if (!stream.CanRead || stream.Length < 256) throw Error("executable", "更新文件不是受支持的通用 Windows EXE。");
        var bytes = new byte[64]; stream.Position = 0; if (stream.Read(bytes, 0, 64) != 64 || bytes[0] != 'M' || bytes[1] != 'Z') throw Error("executable", "更新文件不是受支持的通用 Windows EXE。");
        var offset = BitConverter.ToInt32(bytes, 60);
        if (offset < 64 || offset > stream.Length - 96) throw Error("executable", "更新文件不是受支持的通用 Windows EXE。");
        stream.Position = offset; bytes = new byte[96]; if (stream.Read(bytes, 0, bytes.Length) != bytes.Length || BitConverter.ToUInt32(bytes, 0) != 0x00004550 || BitConverter.ToUInt16(bytes, 4) != 0x014c ||
            (BitConverter.ToUInt16(bytes, 22) & 0x2002) != 0x0002 || BitConverter.ToUInt16(bytes, 24) != 0x010b || BitConverter.ToUInt16(bytes, 92) != 2)
            throw Error("executable", "更新文件不是受支持的通用 Windows EXE。");
    }
    private static void ValidateDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw Error("staging", "下载暂存目录不存在或不是完整路径。");
        for (var cursor = new DirectoryInfo(Path.GetFullPath(directory)); cursor is not null; cursor = cursor.Parent)
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0) throw Error("staging", "下载暂存目录不能包含符号链接或联接。");
    }
}
