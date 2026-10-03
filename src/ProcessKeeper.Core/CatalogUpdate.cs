using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: InternalsVisibleTo("ProcessKeeper.CatalogUpdate.Tests")]
namespace ProcessKeeper.Core;

public sealed record CatalogMetadata(long Revision, string Version, string CheckedOn, string PublishedUtc, int RuleCount, string Sha256, bool BuiltIn);
internal sealed record CatalogData(CatalogMetadata Metadata, IReadOnlyList<UninstallRecommendationRule> Rules);
public sealed class CatalogUpdateOffer
{
    internal byte[] Payload { get; }
    internal byte[] Signature { get; }
    public CatalogMetadata Metadata { get; }
    internal CatalogUpdateOffer(byte[] payload, byte[] signature, CatalogMetadata metadata)
    { Payload = (byte[])payload.Clone(); Signature = (byte[])signature.Clone(); Metadata = metadata; }
}

/// <summary>Fixed-source, signed registration-name hints. Never a code, file-scanning or configuration update.</summary>
public sealed class CatalogUpdater
{
    public const string Repository = "KangQiovo/ProcessKeeper";
    public const string OfficialDirectory = "https://raw.githubusercontent.com/KangQiovo/ProcessKeeper/main/catalog/v1/";
    public const int MaximumPayloadBytes = 512 * 1024;
    public const int MaximumRules = 500, MaximumAliases = 2000;
    public const long BuiltInRevision = 2026093002;
    public const string BuiltInHash = "565FD34384C3F72C590CC8245F9F163AB915014F291D337C073E0B469426390F";
    private const string Modulus = "3A/hI1tAev+8cZBByymaQGPaDvmPTJKxQ8nEz+oBDa917SsJdXdnuPQ2e2sWtJxh79uqv3nfQi2z3K58OQcuMA/5/0CpfkEJc5slCz6WBw9lMphnR5R1mlNd2krxEtTomkKjZOwGpnO0qzfmBq7CkLpALlUCR3stDD+UXC19lzUJg2vfXg+4zj/FtefGHlhiBX8igj8i3/XhiUTfyJ50F64u/rTo3eEeks29uIO2wwlv7+WNB+N7RFMT+le1N7Mw2ZM6qf7Xz7RA6cqXPbln3CsvnlZPQmMWCjWk3k7nUYI3L7Jj3NUBhjhH+ywkEEevXccYLydWAFtV3oO2M9VafwO9T+vl/Ip89lYvrc6sseD2PFn0SUqLumxGTVBtnyQLcE6fy/QU8uXBh5i5gSZruvGCatzO76BbCLQ/iYD3H0LNYbLwuTOXsD2colENGSd/0w7B6LrSt5p4BEwgM9iST8qTLWFDgn/qdV167iSl2o+NsCUNuYuNROXTPZlDEqgF";
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private readonly string _directory, _path;
    private readonly ICatalogTransport _transport;
    private readonly RSAParameters _key;
    private readonly Action<CatalogData>? _activate;
    private CatalogMetadata _current;
    public CatalogMetadata Current => _current;
    public CatalogUpdater(string dataDirectory) : this(dataDirectory, new CatalogHttpTransport(), PublicKey(), UninstallRecommendations.Metadata, UninstallRecommendations.Activate) { }
    internal CatalogUpdater(string directory, ICatalogTransport transport, RSAParameters key, CatalogMetadata baseline, Action<CatalogData>? activate = null)
    {
        _directory = Path.GetFullPath(Path.Combine(directory, "recommendation-catalog")); _path = Path.Combine(_directory, "verified.catalog");
        _transport = transport; _key = key; _current = baseline; _activate = activate;
    }
    public static CatalogMetadata BuiltInMetadata() => new(BuiltInRevision, "2026.09.30.2", "2026-09-30", "", 68, BuiltInHash, true);
    internal static RSAParameters PublicKey() => new() { Modulus = Convert.FromBase64String(Modulus), Exponent = new byte[] { 1, 0, 1 } };
    public async Task LoadAsync(CancellationToken token = default)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() => LoadCache(token), token).ConfigureAwait(false); }
        finally { CacheGate.Release(); }
    }
    private void LoadCache(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); CheckPath();
        if (!File.Exists(_path)) return;
        using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumPayloadBytes + 1024 || file.Length < 400) throw Invalid("规则缓存格式无效，继续保留当前规则。");
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: false);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "PKCAT001") throw Invalid("规则缓存格式无效，继续保留当前规则。");
        int length = reader.ReadInt32(); if (length < 1 || length > MaximumPayloadBytes || file.Length != 12L + length + 384) throw Invalid("规则缓存格式无效，继续保留当前规则。");
        var data = Verify(reader.ReadBytes(length), reader.ReadBytes(384), _key);
        token.ThrowIfCancellationRequested();
        ValidateRevision(data.Metadata);
        if (data.Metadata.Revision > _current.Revision) { _activate?.Invoke(data); _current = data.Metadata; }
    }
    public async Task<CatalogUpdateOffer?> CheckAsync(CancellationToken token = default)
    {
        // Bounded downloads are memory-only; checking never changes the active catalog or its cache.
        var payload = await _transport.DownloadAsync(new Uri(OfficialDirectory + "catalog.json"), MaximumPayloadBytes, token).ConfigureAwait(false);
        var signature = await _transport.DownloadAsync(new Uri(OfficialDirectory + "catalog.sig"), 384, token).ConfigureAwait(false);
        var data = await Task.Run(() => Verify(payload, signature, _key), token).ConfigureAwait(false);
        ValidateRevision(data.Metadata);
        return data.Metadata.Revision == _current.Revision ? null : new(payload, signature, data.Metadata);
    }
    public async Task ApplyAsync(CatalogUpdateOffer offer, CancellationToken token = default)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                CheckPath(); Directory.CreateDirectory(_directory); CheckPath();
                var lockPath = Path.Combine(_directory, "update.lock");
                if (File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0) throw Invalid("规则缓存路径包含重定向，已停止写入。");
                // Cross-process, fail promptly if an earlier instance is still committing.
                using var updateLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // Invalid/obsolete cache data cannot lower the trusted in-memory revision, but a
                // fully verified higher revision can repair it. Access/path failures still abort.
                try { LoadCache(token); }
                catch (Exception ex) when (ex is InvalidDataException or CryptographicException or JsonException or DecoderFallbackException or FormatException or InvalidOperationException) { }
                var data = Verify(offer.Payload, offer.Signature, _key); ValidateRevision(data.Metadata);
                if (data.Metadata.Revision <= _current.Revision) throw Invalid("规则版本未增加，未重复应用。");
                token.ThrowIfCancellationRequested(); CheckPath();
                var temporary = Path.Combine(_directory, ".catalog-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                    using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
                    { writer.Write(Encoding.ASCII.GetBytes("PKCAT001")); writer.Write(offer.Payload.Length); writer.Write(offer.Payload); writer.Write(offer.Signature); writer.Flush(); file.Flush(true); }
                    token.ThrowIfCancellationRequested(); CheckPath();
                    if (File.Exists(_path)) File.Replace(temporary, _path, null); else File.Move(temporary, _path);
                    // Commit is the boundary: cancellation after replacement does not report an undone update.
                    _activate?.Invoke(data); _current = data.Metadata;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }, token).ConfigureAwait(false);
        }
        finally { CacheGate.Release(); }
    }
    private void ValidateRevision(CatalogMetadata metadata)
    {
        if (metadata.Revision < BuiltInRevision || metadata.Revision < _current.Revision) throw Invalid("规则版本倒退，已拒绝更新并保留当前规则。");
        if (metadata.Revision == _current.Revision && metadata.Sha256 != _current.Sha256) throw Invalid("相同规则修订号的内容不一致，已拒绝更新。");
    }
    private void CheckPath()
    {
        for (var path = _path; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("规则缓存路径包含重定向，已停止写入。");
            if (path == Path.GetPathRoot(path)) break;
        }
    }
    internal static CatalogData Verify(byte[] payload, byte[] signature, RSAParameters key)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes || signature.Length != 384) throw Invalid("规则下载大小无效，已保留当前规则。");
        using (var rsa = CreateRsa())
        { rsa.ImportParameters(key); if (!rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw Invalid("规则签名验证失败，已保留当前规则。"); }
        // Strict UTF-8 and JSON only after verifying the exact downloaded bytes.
        var text = new UTF8Encoding(false, true).GetString(payload);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        var root = document.RootElement;
        Fields(root, "schema", "repository", "revision", "version", "publishedUtc", "checkedOn", "matching", "rules");
        if (root.GetProperty("schema").ValueKind != JsonValueKind.Number || !root.GetProperty("schema").TryGetInt32(out var schema) || schema != 1 ||
            Text(root, "repository", 80) != Repository || Text(root, "matching", 32) != "exact-name-v1") throw Invalid("规则库架构、来源或匹配方式不受支持。");
        if (!root.GetProperty("revision").TryGetInt64(out var revision) || revision < BuiltInRevision || revision > 999999999999999) throw Invalid("规则修订号无效。");
        var version = Text(root, "version", 40); if (!Regex.IsMatch(version, @"^[0-9]+(?:\.[0-9]+){1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) throw Invalid("规则版本格式无效。");
        var published = Text(root, "publishedUtc", 20);
        if (!DateTime.TryParseExact(published, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date) || date > DateTime.UtcNow.AddDays(1)) throw Invalid("规则发布日期无效。");
        var checkedOn = Text(root, "checkedOn", 10); Date(checkedOn, false);
        var list = root.GetProperty("rules"); if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > MaximumRules) throw Invalid("规则条目数量无效。");
        var rules = new List<UninstallRecommendationRule>(); var ids = new HashSet<string>(StringComparer.Ordinal); var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in list.EnumerateArray())
        {
            Fields(item, "id", "basis", "names", "reason", "sourceUrl", "sourcePublishedOn");
            var id = Text(item, "id", 64); if (!Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) || !ids.Add(id)) throw Invalid("规则标识重复或无效。");
            var basis = Text(item, "basis", 24) switch { "watchlist" => UninstallRecommendationBasis.UserWatchlist, "published-report" => UninstallRecommendationBasis.PublishedRiskReport, _ => throw Invalid("规则证据类型无效。") };
            var names = item.GetProperty("names"); if (names.ValueKind != JsonValueKind.Array || names.GetArrayLength() is < 1 or > 20) throw Invalid("规则别名数量无效。");
            var actualNames = new List<string>();
            foreach (var alias in names.EnumerateArray())
            {
                var name = String(alias, 160); var normalized = Regex.Replace(name.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ");
                if (name != normalized || !aliases.Add(normalized) || aliases.Count > MaximumAliases) throw Invalid("规则名称重复、未规范化或超过上限。");
                actualNames.Add(name);
            }
            var reason = item.GetProperty("reason"); Fields(reason, "en", "zh-Hans", "zh-Hant");
            var en = Text(reason, "en", 1500); var hans = Text(reason, "zh-Hans", 1500); var hant = Text(reason, "zh-Hant", 1500);
            var url = Text(item, "sourceUrl", 2048, true); var sourceDate = Text(item, "sourcePublishedOn", 10, true); Date(sourceDate, true);
            if (basis == UninstallRecommendationBasis.UserWatchlist ? url.Length != 0 || sourceDate.Length != 0 : !EvidenceUrl(url)) throw Invalid("规则证据链接或关注名单来源无效。");
            rules.Add(new(id, basis, actualNames.AsReadOnly(), hans, url, sourceDate, en, hant));
        }
        using var sha = SHA256.Create(); var hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "");
        return new(new(revision, version, checkedOn, published, rules.Count, hash, false), rules.AsReadOnly());
    }
    internal static RSA CreateRsa()
    {
#if NETFRAMEWORK
        return new RSACryptoServiceProvider(3072, new CspParameters(24) { Flags = CspProviderFlags.CreateEphemeralKey }) { PersistKeyInCsp = false };
#else
        return RSA.Create();
#endif
    }
    private static bool EvidenceUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        !System.Net.IPAddress.TryParse(uri.Host, out _) && (uri.Host == "www.huorong.cn" || uri.Host == "www.malwarebytes.com" || uri.Host == "www.360.cn" || uri.Host == "learn.microsoft.com" || uri.Host == "www.microsoft.com" || uri.Host == "www.eset.com" || uri.Host == "www.qianxin.com");
    private static void Date(string value, bool optional)
    { if (optional && value.Length == 0) return; if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw Invalid("规则来源日期无效。"); }
    private static void Fields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("规则 JSON 格式无效。");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!names.Contains(field.Name) || !found.Add(field.Name)) throw Invalid("规则 JSON 包含未知或重复字段。");
        if (found.Count != names.Length) throw Invalid("规则 JSON 缺少必需字段。");
    }
    private static string Text(JsonElement value, string property, int maximum, bool empty = false) => String(value.GetProperty(property), maximum, empty);
    private static string String(JsonElement value, int maximum, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid("规则字段类型无效。");
        var text = value.GetString()!;
        if ((!empty && text.Length == 0) || text.Length > maximum || text.Any(char.IsControl) || text.IndexOf('\u202e') >= 0 || text.IndexOf('\u2066') >= 0 || text.IndexOf('\u2067') >= 0 || text.IndexOf('\u2068') >= 0 || text.IndexOf('\u2069') >= 0) throw Invalid("规则字段长度或字符无效。");
        return text;
    }
    private static InvalidDataException Invalid(string text) => new(L.T(text));
}

internal interface ICatalogTransport { Task<byte[]> DownloadAsync(Uri uri, int maximumBytes, CancellationToken token); }
internal sealed class CatalogHttpTransport : ICatalogTransport
{
    public async Task<byte[]> DownloadAsync(Uri uri, int maximumBytes, CancellationToken token)
    {
        if (uri.AbsoluteUri != CatalogUpdater.OfficialDirectory + "catalog.json" && uri.AbsoluteUri != CatalogUpdater.OfficialDirectory + "catalog.sig") throw new InvalidDataException(L.T("规则更新地址不受支持。"));
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
#endif
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("ProcessKeeper-RuleCatalog/1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.StatusCode != System.Net.HttpStatusCode.OK) throw new HttpRequestException(L.F($"规则更新请求失败，HTTP {(int)response.StatusCode}。当前规则保持不变。"));
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes) throw new InvalidDataException(L.T("规则下载超过大小上限。"));
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var result = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false); if (count == 0) break;
            if (result.Length + count > maximumBytes) throw new InvalidDataException(L.T("规则下载超过大小上限。"));
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}
