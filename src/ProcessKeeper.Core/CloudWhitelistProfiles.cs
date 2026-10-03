using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProcessKeeper.Core;

public enum CloudProfileFailure { HttpError, RateLimited, InvalidResponse, Changed, NotPortable, InvalidDescriptor, Timeout, TooLarge, RedirectBlocked }
public sealed class CloudWhitelistProfilesException(CloudProfileFailure failure, string message, int? statusCode = null, Exception? inner = null)
    : IOException(message, inner)
{
    public CloudProfileFailure Failure { get; } = failure;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>An immutable public file identity issued by a directory listing, never a configurable URL.</summary>
public sealed class CloudWhitelistProfileDescriptor
{
    internal CloudWhitelistProfileDescriptor(string fileName, string sha, long size)
    { FileName = fileName; Sha = sha; Size = size; }
    public string FileName { get; }
    public string Name => FileName.Substring(0, FileName.Length - 5);
    public string Path => CloudWhitelistProfiles.DirectoryPath + FileName;
    public string Sha { get; }
    public long Size { get; }
    public string Id => Path + "|" + Sha;
}
public sealed record CloudWhitelistProfilePreview(CloudWhitelistProfileDescriptor Descriptor, IReadOnlyList<WhitelistRule> Rules, string Json);

/// <summary>Explicit, unauthenticated reads of portable community rules. Never touches local profiles or settings.</summary>
public sealed class CloudWhitelistProfiles : IDisposable
{
    public const string Repository = "KangQiovo/ProcessKeeper";
    public const string DirectoryPath = "community/profiles/";
    public const int MaximumProfiles = 100, MaximumDirectoryEntries = 200;
    private const int MaximumResponseBytes = RuleFileCodec.MaximumFileBytes * 2;
    private const string ApiPrefix = "https://api.github.com/repos/" + Repository + "/contents/";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _issuedLock = new();
    private readonly HashSet<CloudWhitelistProfileDescriptor> _issued = new();
    private bool _disposed;

    public CloudWhitelistProfiles(HttpClient? client = null)
    {
        _ownsHttp = client is null;
        _http = client ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseDefaultCredentials = false, Credentials = null,
            UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyList<CloudWhitelistProfileDescriptor>> ListAsync(CancellationToken token = default)
    {
        CheckDisposed();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var result = await ListCoreAsync(timeout.Token).ConfigureAwait(false);
            lock (_issuedLock) { _issued.Clear(); foreach (var item in result) _issued.Add(item); }
            return result;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw Failure(CloudProfileFailure.Timeout, "云端白名单请求超时，请稍后重试。", error); }
        catch (Exception error) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw Failure(CloudProfileFailure.Timeout, "云端白名单请求超时，请稍后重试。", error); }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    public async Task<CloudWhitelistProfilePreview> ReadAsync(CloudWhitelistProfileDescriptor descriptor, CancellationToken token = default)
    {
        CheckDisposed();
        lock (_issuedLock)
            if (descriptor is null || !_issued.Contains(descriptor)) throw Failure(CloudProfileFailure.InvalidDescriptor, "请选择本次云端列表中的白名单配置。" );
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Contents API may dereference a symlink. Recheck the actual directory entry type
            // before fetching, then bind the original bytes to the exact immutable listed blob SHA.
            var current = (await ListCoreAsync(timeout.Token).ConfigureAwait(false)).SingleOrDefault(item => item.FileName == descriptor.FileName);
            if (current is null || current.Sha != descriptor.Sha || current.Size != descriptor.Size)
                throw Failure(CloudProfileFailure.Changed, "云端白名单文件已变化，请重新加载列表后选择。" );
            using var document = await GetJsonAsync(new Uri(ApiPrefix + DirectoryPath + Uri.EscapeDataString(descriptor.FileName)), timeout.Token).ConfigureAwait(false);
            // Even a synchronously completed test/cache response must not parse a 1-MiB file on the UI thread.
            return await Task.Run(() => BuildPreview(descriptor, document.RootElement, timeout.Token), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw Failure(CloudProfileFailure.Timeout, "云端白名单请求超时，请稍后重试。", error); }
        catch (Exception error) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        { throw Failure(CloudProfileFailure.Timeout, "云端白名单请求超时，请稍后重试。", error); }
        catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
    }

    private static CloudWhitelistProfilePreview BuildPreview(CloudWhitelistProfileDescriptor descriptor, JsonElement root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var file = ReadDescriptor(root);
        if (file.FileName != descriptor.FileName || file.Path != descriptor.Path)
            throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        if (file.Sha != descriptor.Sha || file.Size != descriptor.Size)
            throw Failure(CloudProfileFailure.Changed, "云端白名单文件已变化，请重新加载列表后选择。" );
        if (String(root, "encoding") != "base64") throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        var encoded = String(root, "content");
        if (encoded.Length > MaximumResponseBytes) throw Failure(CloudProfileFailure.TooLarge, "云端白名单响应超过大小限制。" );
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException error) { throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。", error); }
        if (bytes.Length > RuleFileCodec.MaximumFileBytes) throw Failure(CloudProfileFailure.TooLarge, "云端白名单文件不能超过 1 MiB。" );
        if (bytes.LongLength != descriptor.Size || GitBlobSha(bytes) != descriptor.Sha)
            throw Failure(CloudProfileFailure.Changed, "云端白名单文件已变化，请重新加载列表后选择。" );
        token.ThrowIfCancellationRequested();
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException error) { throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单文件不是有效的 UTF-8。", error); }
        // The BOM participates in the Git hash; permit an editor's UTF-8 BOM only after verification.
        if (text.Length > 0 && text[0] == '\ufeff') text = text.Substring(1);
        IReadOnlyList<WhitelistRule> rules;
        try { rules = RuleFileCodec.Parse(text); }
        catch (InvalidDataException error) { throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单配置格式无效。", error); }
        foreach (var rule in rules)
        {
            token.ThrowIfCancellationRequested();
            if (!Portable(rule)) throw Failure(CloudProfileFailure.NotPortable, "云端白名单仅接受便携应用身份、包族或进程名称规则，不接受本机路径。" );
        }
        return new(descriptor, rules, RuleFileCodec.Serialize(rules));
    }

    private async Task<IReadOnlyList<CloudWhitelistProfileDescriptor>> ListCoreAsync(CancellationToken token)
    {
        using var document = await GetJsonAsync(new Uri(ApiPrefix + DirectoryPath.TrimEnd('/')), token).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        if (document.RootElement.GetArrayLength() > MaximumDirectoryEntries) throw Failure(CloudProfileFailure.TooLarge, "云端白名单目录项目过多，已停止读取。" );
        var result = new List<CloudWhitelistProfileDescriptor>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var descriptor = ReadDescriptor(item);
            if (!names.Add(descriptor.FileName)) throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
            if (!descriptor.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Count >= MaximumProfiles) throw Failure(CloudProfileFailure.TooLarge, "云端白名单配置超过 100 套，已停止读取。" );
            result.Add(descriptor);
        }
        return Array.AsReadOnly(result.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static CloudWhitelistProfileDescriptor ReadDescriptor(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
            if (!properties.Add(property.Name)) throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        if (String(item, "type") != "file" || item.TryGetProperty("target", out _) ||
            item.TryGetProperty("submodule_git_url", out var submodule) && submodule.ValueKind != JsonValueKind.Null)
            throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单目录含目录、链接或子模块，已停止读取。" );
        var name = String(item, "name"); var path = String(item, "path"); var sha = String(item, "sha").ToLowerInvariant();
        if (name.Length == 0 || name.Length > 128 || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal) ||
            name.Contains("..") || name.Any(char.IsControl) || name.IndexOfAny(new[] { '/', '\\', ':', '%', '?', '#', '*', '"', '<', '>', '|' }) >= 0 ||
            path != DirectoryPath + name || sha.Length != 40 || !sha.All(Uri.IsHexDigit) ||
            !item.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size) || size < 0)
            throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var stem = name.Substring(0, name.Length - 5);
            if (stem.Length == 0 || stem.Length > 80 || stem != stem.Trim())
                throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
        }
        if (size > RuleFileCodec.MaximumFileBytes) throw Failure(CloudProfileFailure.TooLarge, "云端白名单文件不能超过 1 MiB。" );
        return new(name, sha, size);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token)
    {
        // An injected client is useful for controlled tests, but must never add credentials to these public requests.
        if (_http.DefaultRequestHeaders.Authorization is not null || _http.DefaultRequestHeaders.Contains("Cookie"))
            throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单请求不能携带身份凭据。" );
        for (var redirect = 0; ; redirect++)
        {
            token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            request.Headers.TryAddWithoutValidation("User-Agent", "ProcessKeeper/1.7.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } final && final != uri)
                throw Failure(CloudProfileFailure.RedirectBlocked, "云端白名单重定向目标不受支持。" );
            var code = (int)response.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                var next = location is null ? null : location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (redirect >= 3 || next is null || next.Scheme != "https" || next.Host != "api.github.com" || next.Port != 443 ||
                    next.UserInfo.Length > 0 || next.Query.Length > 0 || next.Fragment.Length > 0 || next.AbsolutePath != uri.AbsolutePath)
                    throw Failure(CloudProfileFailure.RedirectBlocked, "云端白名单重定向目标不受支持。" );
                uri = next; continue;
            }
            if (code == 429 || code == 403 && (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0") || response.Headers.RetryAfter is not null))
                throw new CloudWhitelistProfilesException(CloudProfileFailure.RateLimited, L.T("GitHub 请求次数已达限制，请稍后重试。"), code);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new CloudWhitelistProfilesException(CloudProfileFailure.HttpError, L.F($"云端白名单读取失败 | HTTP {code}"), code);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw Failure(CloudProfileFailure.TooLarge, "云端白名单响应超过大小限制。" );
            using var cancelled = token.Register(() => response.Dispose());
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream(); var buffer = new byte[16384];
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumResponseBytes) throw Failure(CloudProfileFailure.TooLarge, "云端白名单响应超过大小限制。" );
                output.Write(buffer, 0, read);
            }
            token.ThrowIfCancellationRequested();
            var bytes = output.ToArray();
            try { return await Task.Run(() => JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 16 }), token).ConfigureAwait(false); }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            { throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。", error); }
        }
    }

    private static string String(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw Failure(CloudProfileFailure.InvalidResponse, "云端白名单响应格式无效。" );
    private static bool Portable(WhitelistRule rule)
    {
        if (rule.Value.Length > 255 || rule.Value.Any(char.IsControl)) return false;
        if (rule.Kind == RuleKind.ProcessName) return rule.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            rule.Value.Length > 4 && rule.Value.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) < 0;
        if (rule.Kind != RuleKind.Application) return false;
        var separator = rule.Value.IndexOf(':'); if (separator < 0) return false;
        var prefix = rule.Value.Substring(0, separator); var value = rule.Value.Substring(separator + 1);
        if (value.Length == 0) return false;
        bool AsciiIdentity(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.';
        if (!value.All(AsciiIdentity)) return false;
        return prefix.Equals("known", StringComparison.OrdinalIgnoreCase) && !value.Contains(".") ||
            prefix.Equals("package", StringComparison.OrdinalIgnoreCase) && value.IndexOf('_') > 0 && value.LastIndexOf('_') < value.Length - 1;
    }
    private static string GitBlobSha(byte[] bytes)
    {
        using var sha = SHA1.Create(); var header = Encoding.ASCII.GetBytes("blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\0");
        sha.TransformBlock(header, 0, header.Length, header, 0); sha.TransformFinalBlock(bytes, 0, bytes.Length);
        return BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
    }
    private static CloudWhitelistProfilesException Failure(CloudProfileFailure code, string message, Exception? inner = null) => new(code, L.T(message), inner: inner);
    private void CheckDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(CloudWhitelistProfiles)); }
    public void Dispose() { if (_disposed) return; _disposed = true; if (_ownsHttp) _http.Dispose(); lock (_issuedLock) _issued.Clear(); }
}
