using System.Reflection;
using ProcessKeeper.Core;

internal static class InstallerIdentityVerification
{
    public static void Run(Action<bool, string> check)
    {
        var validate = typeof(UpdateInstaller).GetMethod("ValidateDownloadIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
        var hash = new string('a', 64);
        var asset = new UpdateAsset(42, "ProcessKeeper.exe", 8192,
            "https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.5.1-beta.1/ProcessKeeper.exe", "sha256:" + hash, true, "");
        var release = new UpdateRelease(UpdatePolicy.Repository, "v1.5.1-beta.1", "1.5.1-beta.1", "test", null, "",
            "https://github.com/KangQiovo/ProcessKeeper/releases/tag/v1.5.1-beta.1", true, new[] { asset });
        var download = new UpdateDownloadResult("unused: identity checks do not open this file", hash, "official", release, asset);
        bool Accepted(UpdateDownloadResult value)
        {
            try { validate.Invoke(null, new object[] { value }); return true; }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { return false; }
        }
        check(Accepted(download), "installer accepts internally consistent fixed-repository prerelease provenance without IO");
        check(Accepted(download with { SourceId = "ghfast" }), "binary mirror selection never changes official repository identity");
        check(!Accepted(download with { Release = release with { Repository = "fork/ProcessKeeper" } }), "installer rejects another repository before opening any file");
        check(!Accepted(download with { Release = release with { Version = "1.5.2" } }), "installer rejects tag/version mismatch");
        check(!Accepted(download with { Sha256 = new string('b', 64) }), "installer rejects result digest not matching official asset digest");
        check(!Accepted(download with { SourceId = "arbitrary mirror" }), "installer rejects unknown transport identity");
        check(!Accepted(download with { Release = release with { Assets = Array.Empty<UpdateAsset>() } }), "installer requires asset membership in official release");
        check(!Accepted(download with { Release = release with { Assets = new[] { asset, asset } } }), "installer refuses ambiguous duplicate asset identity");
        check(!Accepted(download with { Asset = asset with { Size = 8193 } }), "installer refuses altered asset metadata after download");
        check(!Accepted(download with { Release = release with { HtmlUrl = "https://github.com/fork/ProcessKeeper/releases/tag/v1.5.1-beta.1" } }), "installer refuses forged release page authority");
        foreach (var bad in new[] {
            asset.DownloadUrl.Replace("KangQiovo/ProcessKeeper", "fork/ProcessKeeper"),
            asset.DownloadUrl.Replace("KangQiovo", "%4BangQiovo"),
            asset.DownloadUrl + "?redirect=1", asset.DownloadUrl.Replace("github.com", "github.com.evil.invalid"),
            asset.DownloadUrl.Replace("/releases/download/", "/other/../releases/download/") })
        {
            var changed = asset with { DownloadUrl = bad };
            check(!Accepted(download with { Asset = changed, Release = release with { Assets = new[] { changed } } }), "installer rejects noncanonical official asset URL before any IO");
        }
        var unavailable = asset with { CanAutoInstall = false };
        check(!Accepted(download with { Asset = unavailable, Release = release with { Assets = new[] { unavailable } } }), "installer honors non-installable asset restrictions independently");
        foreach (var name in new[] { "Other.exe", "../ProcessKeeper.exe" })
        {
            var renamed = asset with { Name = name, DownloadUrl = "https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.5.1-beta.1/" + Uri.EscapeDataString(name) };
            check(!Accepted(download with { Asset = renamed, Release = release with { Assets = new[] { renamed } } }), "installer independently validates executable name despite a supplied eligibility flag");
        }
        var restricted = asset with { Restriction = "not eligible" };
        check(!Accepted(download with { Asset = restricted, Release = release with { Assets = new[] { restricted } } }), "conflicting eligibility and restriction is rejected");
        check(typeof(UpdateInstaller).GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == "Prepare")
            .All(method => method.GetParameters().Length == 3 && method.GetParameters()[1].ParameterType == typeof(UpdateDownloadResult)),
            "public installer has no bare path/hash/version preparation bypass");
    }
}
