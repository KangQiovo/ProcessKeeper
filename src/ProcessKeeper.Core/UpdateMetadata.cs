using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

public sealed partial class UpdateService
{
    private UpdateRelease ParseRelease(JsonElement item)
    {
        const string repository = UpdatePolicy.Repository;
        var tag = Text(item, "tag_name", 256);
        var version = UpdateVersion.TryParse(tag, out var parsed) ? parsed!.Value : "";
        var html = Text(item, "html_url");
        var expectedPage = "https://github.com/" + repository + "/releases/tag/" + Uri.EscapeDataString(tag);
        if (tag.Length == 0 || !ExactUrl(html, expectedPage)) throw Error("metadata", "GitHub 发布链接与所选仓库不一致。");
        DateTimeOffset? published = DateTimeOffset.TryParse(Text(item, "published_at", 100), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
        var assets = new List<UpdateAsset>(); var identities = new HashSet<long>();
        if (item.TryGetProperty("assets", out var raw) && raw.ValueKind == JsonValueKind.Array)
            foreach (var value in raw.EnumerateArray().Take(100))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("id", out var identifier) || identifier.ValueKind != JsonValueKind.Number || !identifier.TryGetInt64(out var id) || id <= 0 || !identities.Add(id))
                    throw Error("metadata", "GitHub 发布文件身份无效或重复。");
                var name = Text(value, "name", 512); var download = Text(value, "browser_download_url"); var digest = Text(value, "digest", 256).ToLowerInvariant();
                if (!value.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number || !sizeValue.TryGetInt64(out var size))
                    throw Error("metadata", "GitHub 发布信息格式无效。");
                var expectedDownload = "https://github.com/" + repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
                if (!ExactUrl(download, expectedDownload)) throw Error("metadata", "更新文件链接与固定官方仓库不一致，已拒绝发布信息。");
                var target = UpdatePackagePolicy.Identify(name, version);
                var restriction = version.Length == 0 ? L.T("发布标签不是有效的 SemVer，不能自动安装。") :
                    !PortableVersion(version) ? L.T("发布版本超出 Windows EXE 版本字段范围，不能自动安装。") :
                    !PortableAssetName(name) ? L.T("仅支持 ProcessKeeper 通用 EXE 更新文件。") :
                    !UpdatePackagePolicy.Supports(target, _runtime) ? L.T("此更新文件不支持当前系统、界面版本或处理器架构。") :
                    size <= 0 || size > MaximumDownloadBytes ? L.T("更新文件大小必须在 1 字节至 512 MiB 之间。") :
                    Text(value, "state", 40) != "uploaded" ? L.T("更新文件尚未上传完成。") :
                    !ValidDigest(digest) ? L.T("官方未提供 SHA-256 摘要，不能自动安装。") : "";
                assets.Add(new UpdateAsset(id, name, size, download, digest, restriction.Length == 0, restriction, target));
            }
        if (_runtime.PackageFlavor == UpdatePackageTarget.Unsupported && assets.Any(asset => asset.CanAutoInstall && asset.PackageTarget != UpdatePackageTarget.Universal))
            for (int index = 0; index < assets.Count; ++index)
                if (assets[index].PackageTarget == UpdatePackageTarget.Universal) assets[index] = assets[index] with { CanAutoInstall = false, Restriction = L.T("更新文件类型与当前应用包不一致。") };
        var body = Text(item, "body", 128 * 1024);
        if (body.Length == 128 * 1024) body += "\n" + L.T("发布说明过长，已截断；完整内容请查看官方发布页面。");
        return new UpdateRelease(repository, tag, version, Text(item, "name", 512), published, body, html, True(item, "prerelease"), assets.AsReadOnly());
    }
    internal static bool PortableAssetName(string name) =>
        Regex.IsMatch(name, @"\AProcessKeeper(?:[A-Za-z0-9._ -]{0,120})\.exe\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0;
    internal static bool PortableVersion(string version) => version.Split(new[] { '-', '+' })[0].Split('.').All(part =>
        ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    internal static bool ExactUrl(string actual, string expected) => actual.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) &&
        actual.Substring("https://github.com".Length).Equals(expected.Substring("https://github.com".Length), StringComparison.Ordinal) &&
        Uri.TryCreate(actual, UriKind.Absolute, out var a) && Uri.TryCreate(expected, UriKind.Absolute, out var b) &&
        a.Scheme == "https" && a.Port == 443 && a.UserInfo.Length == 0 && a.Query.Length == 0 && a.Fragment.Length == 0 &&
        a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.AbsolutePath.Equals(b.AbsolutePath, StringComparison.Ordinal);
    internal static bool ValidDigest(string value) => value.Length == 71 && value.StartsWith("sha256:", StringComparison.Ordinal) && value.Substring(7).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    private static bool GithubDownloadHost(string host) => host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";
    private static void RequireDownloadUri(Uri uri, bool mirror)
    {
        var permittedHost = GithubDownloadHost(uri.Host) || mirror && UpdateSources.All.Where(s => !s.IsOfficial).Any(s =>
        {
            var host = new Uri(s.Prefix).Host;
            return uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
        });
        if (uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback || !permittedHost)
            throw Error("unsafe-url", "下载链接或重定向不在允许的 HTTPS 来源范围内。");
    }
    private static void ValidateEffectiveUri(HttpResponseMessage response, Uri requested, bool metadata, bool mirror)
    {
        var effective = response.RequestMessage?.RequestUri ?? requested;
        if (metadata)
        {
            if (effective.AbsoluteUri != requested.AbsoluteUri) throw Error("metadata", "发布信息必须来自 GitHub 官方接口。");
        }
        else RequireDownloadUri(effective, mirror);
    }
}
