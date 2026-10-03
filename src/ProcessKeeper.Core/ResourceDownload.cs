using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace ProcessKeeper.Core;

public sealed record ResourceDownloadRequest(string Url, string Directory, string FileName,
    string UserAgent = "ProcessKeeper/1.7.0", int Connections = 16, string SourceId = "official", string ExpectedSha256 = "", bool AllowThirdParty = false);
public sealed record ResourceDownloadProgress(long Bytes, long? Total, double BytesPerSecond, string Source, string State,
    int ActiveConnections = 0, int Segments = 0, string FallbackReason = "");
public sealed record ResourceDownloadResult(string Path, long Bytes, string Sha256, string Source);
public sealed record ResourceDownloadSource(string Id, string Name, string Prefix);
public static class ResourceDownloadSources
{
    public static IReadOnlyList<ResourceDownloadSource> All { get; } = Array.AsReadOnly(new[]
    {
        new ResourceDownloadSource("official", "Direct", ""), new ResourceDownloadSource("auto", "Auto", ""),
        new ResourceDownloadSource("ghfast", "ghfast.top", "https://ghfast.top/"),
        new ResourceDownloadSource("ghproxy", "ghproxy.net", "https://ghproxy.net/"),
        new ResourceDownloadSource("gh-proxy", "gh-proxy.com", "https://gh-proxy.com/"),
        new ResourceDownloadSource("llkk", "gh.llkk.cc", "https://gh.llkk.cc/"),
        new ResourceDownloadSource("homeboyc", "ghproxy.homeboyc.cn", "https://ghproxy.homeboyc.cn/"),
        new ResourceDownloadSource("gh-proxy-org", "gh-proxy.org", "https://gh-proxy.org/")
    });
}
public sealed partial class ResourceDownloadJob : IDisposable
{
    private readonly ResourceDownloadRequest _request;
    private readonly Uri _original;
    private readonly string _target, _work;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly object _sync = new();
    private CancellationTokenSource? _active;
    private bool _running, _disposed;
    private string _resumeKey = "";
    private long _received, _lastReport, _lastReportedBytes;
    private readonly Stopwatch _clock = new();
    // A supplied client retains ownership and must expose redirects to this
    // boundary rather than following them automatically or adding credentials.
    public ResourceDownloadJob(ResourceDownloadRequest request, HttpClient? client = null)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _original = NormalizeUrl(request.Url);
        if (!Path.IsPathFullyQualified(request.Directory)) throw new ArgumentException("Choose an absolute download directory");
        if (string.IsNullOrWhiteSpace(request.FileName) || request.FileName != Path.GetFileName(request.FileName) ||
            request.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || request.FileName.EndsWith(".", StringComparison.Ordinal) ||
            request.FileName.EndsWith(" ", StringComparison.Ordinal) || IsReserved(request.FileName)) throw new ArgumentException("Invalid Windows file name");
        if (request.Connections < 1 || request.Connections > 32) throw new ArgumentException("Connections must be between 1 and 32");
        if (request.UserAgent.Length > 512 || request.UserAgent.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("Invalid User-Agent");
        if (request.ExpectedSha256.Length != 0 && (request.ExpectedSha256.Length != 64 || request.ExpectedSha256.Any(c => !Uri.IsHexDigit(c))))
            throw new ArgumentException("SHA256 must contain 64 hexadecimal characters");
        _target = Path.Combine(Path.GetFullPath(request.Directory), request.FileName);
        _work = Path.Combine(Path.GetDirectoryName(_target)!, ".pk-download-" + Guid.NewGuid().ToString("N"));
        _ownsClient = client is null;
        _http = client ?? CreateClient();
    }
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, AllowAutoRedirect = false };
#if !NETFRAMEWORK
        handler.MaxConnectionsPerServer = 32;
#endif
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static Uri NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Only HTTP and HTTPS file links without credentials or fragments are supported");
        var segments = uri.AbsolutePath.Split('/');
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 6 && segments[3] == "blob")
            uri = new Uri("https://raw.githubusercontent.com/" + segments[1] + "/" + segments[2] + "/" + string.Join("/", segments.Skip(4)) + uri.Query);
        return uri;
    }
    public static bool CanUseGitHubMirror(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.Query.Length == 0 &&
        (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/releases/download/")));
    public static bool CanAccelerate(Uri uri) => CanUseGitHubMirror(uri);
    private static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             stem.Length == 4 && stem[3] >= '1' && stem[3] <= '9');
    }
    private IEnumerable<(ResourceDownloadSource Source, Uri Url)> Candidates()
    {
        var source = ResourceDownloadSources.All.SingleOrDefault(s => s.Id == _request.SourceId) ?? throw new ArgumentException("Unknown download source");
        if (source.Id == "official") return new[] { (source, _original) };
        if (source.Id == "auto" && !CanUseGitHubMirror(_original)) return new[] { (ResourceDownloadSources.All[0], _original) };
        if (!CanUseGitHubMirror(_original)) throw new ArgumentException("GitHub mirrors require public release or raw file links without a query");
        if (!_request.AllowThirdParty) throw new ArgumentException("Third-party download sources require explicit consent");
        return (source.Id == "auto" ? ResourceDownloadSources.All.Where(s => s.Id != "auto") : new[] { source })
            .Select(s => (s, s.Prefix.Length == 0 ? _original : new Uri(s.Prefix + _original.AbsoluteUri)));
    }
    private sealed record Metadata(ResourceDownloadSource Source, Uri Url, long? Length, bool Ranges, string Validator, bool ETag, long Latency,
        string ObservedTag, string ObservedModified);
    public async Task<ResourceDownloadResult> RunAsync(IProgress<ResourceDownloadProgress>? progress = null, CancellationToken token = default)
    {
        CancellationTokenSource life;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ResourceDownloadJob));
            if (_running) throw new InvalidOperationException("A download is already running");
            _running = true; _active = life = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        try
        {
            var candidates = Candidates().ToArray();
            life.Token.ThrowIfCancellationRequested();
            if (File.Exists(_target)) throw new IOException("The target already exists; choose a different file name");
            _clock.Restart(); _lastReport = 0;
            progress?.Report(new(0, null, 0, "", "probing"));
            var probes = await Task.WhenAll(candidates.Select(async c =>
            {
                try { return (Value: await Probe(c.Source, c.Url, life.Token).ConfigureAwait(false), Error: (Exception?)null); }
                catch (Exception error) when (!life.IsCancellationRequested) { return (Value: (Metadata?)null, Error: error); }
            })).ConfigureAwait(false);
            var available = probes.Where(p => p.Value is not null).Select(p => p.Value!).OrderBy(p => p.Latency).ToArray();
            if (available.Length == 0) throw probes.Select(p => p.Error).FirstOrDefault(e => e is not null) ?? new HttpRequestException("No reachable download source");
            Exception? failure = null;
            foreach (var metadata in available)
            {
                life.Token.ThrowIfCancellationRequested();
                try
                {
                    var key = metadata.Url.AbsoluteUri + "|" + metadata.Length + "|" + metadata.Validator;
                    if (_resumeKey != key || metadata.Validator.Length == 0) Cleanup();
                    _resumeKey = key;
                    Directory.CreateDirectory(_work);
                    var completed = Path.Combine(_work, "completed.tmp");
                    _received = 0;
                    var parallel = metadata.Ranges && metadata.Length >= MinimumSplitRemaining && _request.Connections > 1;
                    if (parallel)
                    {
                        IReadOnlyList<AdaptiveSegment>? spans = null;
                        try { spans = await DownloadAdaptive(metadata, progress, life.Token).ConfigureAwait(false); }
                        catch (RangeUnavailableException) when (!life.IsCancellationRequested)
                        {
                            Cleanup(); Directory.CreateDirectory(_work); _received = 0;
                            _fallbackReason = "range-rejected";
                            await DownloadSequential(metadata, completed, progress, life.Token).ConfigureAwait(false);
                        }
                        if (spans is not null)
                        {
                            // A previous sequential fallback may have been paused with a
                            // partial completed.tmp. Always rebuild it from settled ranges.
                            using var output = new FileStream(completed, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                            foreach (var span in spans)
                            {
                                using var input = new FileStream(span.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                                await input.CopyToAsync(output, 65536, life.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    else
                    {
                        _received = 0;
                        _fallbackReason = !metadata.Ranges ? "range-not-supported" : !metadata.Length.HasValue ? "length-unknown" :
                            _request.Connections == 1 ? "single-connection" : "small-file";
                        await DownloadSequential(metadata, completed, progress, life.Token).ConfigureAwait(false);
                    }
                    life.Token.ThrowIfCancellationRequested();
                    var size = new FileInfo(completed).Length;
                    if (metadata.Length.HasValue && metadata.Length.Value != size) throw new InvalidDataException("Downloaded length does not match the server");
                    progress?.Report(new(size, metadata.Length, 0, metadata.Source.Name, "verifying"));
                    string digest;
                    using (var input = new FileStream(completed, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                        digest = await HashAsync(input, life.Token).ConfigureAwait(false);
                    if (_request.ExpectedSha256.Length != 0 && !digest.Equals(_request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("SHA256 verification failed");
                    life.Token.ThrowIfCancellationRequested();
                    File.Move(completed, _target);
                    Cleanup();
                    progress?.Report(new(size, size, 0, metadata.Source.Name, "completed"));
                    return new(_target, size, digest, metadata.Source.Name);
                }
                catch (Exception error) when (!life.IsCancellationRequested && !File.Exists(_target))
                { failure = error; if (available.Length == 1) throw; }
            }
            throw failure ?? new HttpRequestException("All sources failed");
        }
        catch (Exception) when (life.IsCancellationRequested) { throw new OperationCanceledException(life.Token); }
        finally
        {
            lock (_sync) { _running = false; _active = null; life.Dispose(); if (_disposed) ReleaseOwnedResources(); }
        }
    }
    private HttpRequestMessage Request(Uri uri)
    {
#if NETFRAMEWORK
        // .NET Framework uses a per-endpoint ServicePoint instead of this handler property.
        // This is process scoped; no OS or machine-wide networking setting is changed.
        ServicePointManager.FindServicePoint(uri).ConnectionLimit = Math.Max(2, _request.Connections);
#endif
        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (_request.UserAgent.Length != 0) message.Headers.TryAddWithoutValidation("User-Agent", _request.UserAgent);
        message.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        return message;
    }
    private static readonly string[] RedirectHeaders =
        { "User-Agent", "Accept-Encoding", "Range", "If-Range", "If-Match", "If-Unmodified-Since", "If-None-Match", "If-Modified-Since" };
    private async Task<HttpResponseMessage> SendFollowingRedirects(HttpRequestMessage request, CancellationToken token)
    {
        const int maximumRedirects = 10;
        var current = request;
        var ownsCurrent = false;
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                token.ThrowIfCancellationRequested();
                var response = await _http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                    HttpStatusCode.TemporaryRedirect) && (int)response.StatusCode != 308) return response;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (redirects >= maximumRedirects) throw new InvalidDataException("Too many download redirects");
                    var location = response.Headers.Location ?? throw new InvalidDataException("Download redirect has no target");
                    var next = location.IsAbsoluteUri ? location : new Uri(current.RequestUri!, location);
                    if ((next.Scheme != "https" && next.Scheme != "http") || next.UserInfo.Length != 0 || next.Fragment.Length != 0 ||
                        (current.RequestUri!.Scheme == "https" && next.Scheme != "https"))
                        throw new InvalidDataException("Invalid or insecure download redirect");
                    var replacement = Request(next);
                    try
                    {
                        replacement.Version = request.Version;
                        // Copy resource-transfer headers only; do not copy Authorization,
                        // Cookie or a stale Host header from the previous request.
                        foreach (var name in RedirectHeaders)
                            if (request.Headers.TryGetValues(name, out var values))
                            { replacement.Headers.Remove(name); replacement.Headers.TryAddWithoutValidation(name, values); }
                    }
                    catch { replacement.Dispose(); throw; }
                    if (ownsCurrent) current.Dispose();
                    current = replacement; ownsCurrent = true;
                }
                finally { response.Dispose(); }
            }
        }
        finally { if (ownsCurrent) current.Dispose(); }
    }
    private async Task<Metadata> Probe(ResourceDownloadSource source, Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var request = Request(uri); request.Headers.Range = new RangeHeaderValue(0, 0);
        var timer = Stopwatch.StartNew();
        using var response = await SendFollowingRedirects(request, timeout.Token).ConfigureAwait(false);
        CheckResponse(response, uri);
        var ranged = response.StatusCode == HttpStatusCode.PartialContent;
        var range = response.Content.Headers.ContentRange;
        if (ranged && (range is null || range.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is null || range.Length <= 0))
            throw new InvalidDataException("The server returned an invalid probe Content-Range");
        var length = ranged ? range!.Length : response.Content.Headers.ContentLength;
        var tag = response.Headers.ETag;
        var strongTag = tag is not null && !tag.IsWeak;
        var modified = response.Content.Headers.LastModified;
        // RFC 9110 permits dates only without an entity tag and with a strong
        // date validator. Sixty seconds is our conservative clock-skew margin.
        var strongDate = tag is null && modified.HasValue && response.Headers.Date.HasValue &&
            response.Headers.Date.Value - modified.Value >= TimeSpan.FromSeconds(60);
        var validator = strongTag ? tag!.ToString() : strongDate ? modified!.Value.ToString("R") : "";
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (source.Prefix.Length > 0 && contentType == "text/html" && !Path.GetExtension(_original.AbsolutePath).Equals(".html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The proxy returned a web page instead of the requested file");
        return new(source, uri, length, ranged, validator, strongTag, timer.ElapsedMilliseconds,
            tag?.ToString() ?? "", modified?.ToString("R") ?? "");
    }
    private static void CheckResponse(HttpResponseMessage response, Uri requested)
    {
        response.EnsureSuccessStatusCode();
        var effective = response.RequestMessage?.RequestUri;
        if (effective is null || effective.UserInfo.Length != 0 || (effective.Scheme != "http" && effective.Scheme != "https") ||
            (requested.Scheme == "https" && effective.Scheme != "https")) throw new InvalidDataException("Invalid or insecure download redirect");
        if (response.Content.Headers.ContentEncoding.Any(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Encoded range responses are not supported");
    }
    private sealed class RangeUnavailableException : IOException { public RangeUnavailableException() : base("Server stopped supporting byte ranges") { } }
    private static void ValidateVersion(Metadata metadata, HttpResponseMessage response)
    {
        var actual = metadata.ETag ? response.Headers.ETag?.ToString() : response.Content.Headers.LastModified?.ToString("R");
        if (metadata.Validator.Length != 0 && actual is not null && !string.Equals(actual, metadata.Validator, StringComparison.Ordinal))
            throw new InvalidDataException("File changed since the source probe");
        var observedTag = response.Headers.ETag?.ToString();
        var observedModified = response.Content.Headers.LastModified?.ToString("R");
        if (metadata.ObservedTag.Length > 0 && observedTag is not null && observedTag != metadata.ObservedTag ||
            metadata.ObservedModified.Length > 0 && observedModified is not null && observedModified != metadata.ObservedModified)
            throw new InvalidDataException("File metadata changed since the source probe");
    }
    private async Task DownloadSequential(Metadata metadata, string path, IProgress<ResourceDownloadProgress>? progress, CancellationToken token)
    {
        Volatile.Write(ref _activeConnections, 1); Volatile.Write(ref _segmentCount, 1);
        try
        {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _received = 0; StartTransferMeasurement();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using var request = Request(metadata.Url);
                if (metadata.ETag) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(metadata.Validator));
                else if (metadata.Validator.Length != 0) request.Headers.IfUnmodifiedSince = DateTimeOffset.Parse(metadata.Validator, CultureInfo.InvariantCulture);
                using var response = await SendFollowingRedirects(request, timeout.Token).ConfigureAwait(false); CheckResponse(response, metadata.Url);
                if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Expected a complete file response");
                ValidateVersion(metadata, response);
                if (metadata.Length.HasValue && response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != metadata.Length)
                    throw new InvalidDataException("File changed since the source probe");
                using var cancelled = timeout.Token.Register(() => response.Dispose());
                using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                await Copy(input, output, metadata.Length, metadata, progress, timeout).ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!token.IsCancellationRequested && attempt < 2) { await Task.Delay(200 * (attempt + 1), token).ConfigureAwait(false); }
        }
        }
        finally { Volatile.Write(ref _activeConnections, 0); }
    }
    private async Task Copy(Stream input, Stream output, long? expected, Metadata metadata, IProgress<ResourceDownloadProgress>? progress, CancellationTokenSource timeout)
    {
        var buffer = new byte[65536]; long written = 0;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
            if (read == 0) break;
            if (expected.HasValue && written + read > expected.Value) throw new InvalidDataException("Server sent extra bytes");
            await output.WriteAsync(buffer, 0, read, timeout.Token).ConfigureAwait(false);
            written += read; Interlocked.Add(ref _received, read); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            ReportTransfer(metadata, progress);
        }
        if (expected.HasValue && written != expected.Value) throw new InvalidDataException("Server truncated the file");
    }
    public void Pause() { lock (_sync) _active?.Cancel(); }
    private void StartTransferMeasurement()
    { lock (_clock) { _clock.Restart(); _lastReport = 0; _lastReportedBytes = Interlocked.Read(ref _received); } }
    private static async Task<string> HashAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var sha = SHA256.Create();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, buffer, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
    }
    private void Cleanup()
    {
        if (Directory.Exists(_work))
        {
            if ((File.GetAttributes(_work) & FileAttributes.ReparsePoint) != 0) throw new IOException("Download staging directory was redirected");
            // This private directory contains files only. Never recurse through user-created subdirectories or junctions.
            foreach (var path in Directory.GetFiles(_work)) File.Delete(path);
            Directory.Delete(_work, false);
        }
        lock (_adaptiveSync) _segments.Clear();
    }
    private void ReleaseOwnedResources()
    {
        try { Cleanup(); }
        finally { if (_ownsClient) _http.Dispose(); }
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true; _active?.Cancel();
            if (!_running) ReleaseOwnedResources();
        }
    }
}
