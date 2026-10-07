using System.Net;
using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class UpdateAssetSelectionVerification
{
    private static readonly string[] PackageNames =
    {
        "ProcessKeeper-v1.8.0-win7-x86-compat.exe", "ProcessKeeper-v1.8.0-win10-x86-x64.exe",
        "ProcessKeeper-v1.8.0-win10-arm64.exe", "ProcessKeeper-v1.8.0.exe",
        "ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe", "ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe",
        "ProcessKeeper-v1.8.0-win10-arm64-setup.exe"
    };
    internal static async Task Run(Action<bool, string> check)
    {
        // Interleave ancillary files before and among the packages so a filtered index
        // cannot accidentally refer to the full release inventory.
        var names = new[]
        {
            "SHA256SUMS", PackageNames[0], "README.md", PackageNames[1], "ProcessKeeper.Updater.exe", PackageNames[2],
            "SHA256SUMS.txt", PackageNames[3], "README.pdf", PackageNames[4], "ProcessKeeper-debug.exe", PackageNames[5],
            "release-notes.json", PackageNames[6], "ProcessKeeper-v1.9.0.exe", "ProcessKeeper-v1.8.0.zip", "Other.exe",
            "ProcessKeeper-v1.8.0-setup.exe"
        };
        var runtime = new UpdateRuntimeIdentity(10, 0, 26100, 0x8664, false, PackageFlavor: UpdatePackageTarget.Windows10x64);
        using var handler = new Handler(names);
        using var service = new UpdateService(handler, runtime: runtime);
        var release = (await service.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.7.1")).LatestRelease!;
        check(release.Assets.Count == 18 && release.Assets[0].Name == "SHA256SUMS", "official inventory retains checksum, documentation and every uploaded asset");
        var choices = UpdatePackagePolicy.SelectableAssets(release);
        check(choices.Select(asset => asset.Name).SequenceEqual(PackageNames), "package choices exclude checksums, documentation, unrelated executables and mismatched release filenames");
        check(choices.All(asset => release.Assets.Any(original => ReferenceEquals(original, asset))), "package choices retain original official asset identities, URLs, digests and restrictions");
        check(choices is IList<UpdateAsset> { IsReadOnly: true }, "shared choices cannot mutate the official release inventory");
        var preferred = UpdatePackagePolicy.DefaultAsset(release, runtime);
        check(preferred is not null && ReferenceEquals(choices[1], preferred) && release.Assets.IndexOf(preferred) == 3,
            "default package maps to its filtered position even when a checksum precedes it");
        check(choices.Single(asset => asset.Name == PackageNames[2]) is { CanAutoInstall: false, Restriction.Length: > 0 },
            "recognized ARM package remains visible with its existing x64 incompatibility restriction");
        var universal = choices.Single(asset => asset.Name == PackageNames[3]);
        check(universal.CanAutoInstall && UpdatePackagePolicy.IsPackageChange(universal, runtime), "compatible alternative edition remains a deliberate manual package change");
        var installedRuntime = runtime with { DistributionKind = UpdateDistributionKind.Installer };
        check(UpdatePackagePolicy.DefaultAsset(release, installedRuntime)?.Name == PackageNames[5], "installer default remains the matching setup rather than an earlier portable or checksum");
        var missingDigest = release with { Assets = release.Assets.Select(asset => asset.Name == PackageNames[1]
            ? asset with { Digest = "", CanAutoInstall = false, Restriction = "Missing official digest" } : asset).ToArray() };
        var restricted = UpdatePackagePolicy.SelectableAssets(missingDigest).Single(asset => asset.Name == PackageNames[1]);
        check(!restricted.CanAutoInstall && restricted.Digest.Length == 0 && restricted.Restriction == "Missing official digest" &&
            UpdatePackagePolicy.DefaultAsset(missingDigest, runtime) is null, "display filtering never grants eligibility or replaces missing official digest evidence");
        var helperOnly = release with { Assets = release.Assets.Where(asset => asset.Name == "ProcessKeeper-debug.exe").ToArray() };
        check(UpdatePackagePolicy.SelectableAssets(helperOnly).Count == 0 && UpdatePackagePolicy.DefaultAsset(helperOnly, runtime with { PackageFlavor = UpdatePackageTarget.Universal }) is null,
            "unrelated ProcessKeeper helper cannot become the automatic Universal choice");
        var ancillaryOnly = release with { Assets = release.Assets.Where(asset => asset.Name is "SHA256SUMS" or "README.md").ToArray() };
        check(UpdatePackagePolicy.SelectableAssets(ancillaryOnly).Count == 0 && ancillaryOnly.Assets.Count == 2, "ancillary-only release has no update choices without deleting inventory evidence");
        using var historicalService = new UpdateService(new Handler(new[] { "SHA256SUMS", "ProcessKeeper.exe" }, "1.5.1"), runtime: runtime);
        var historical = (await historicalService.CheckAsync(new(), "1.5.0")).LatestRelease!;
        check(UpdatePackagePolicy.SelectableAssets(historical).Single().Name == "ProcessKeeper.exe", "historical unversioned Universal filename remains selectable");
        using var previewService = new UpdateService(new Handler(new[] { "ProcessKeeper-v1.8.0-rc.1.exe", "ProcessKeeper-v1.8.0.exe", "SHA256SUMS" }, "1.8.0-rc.1"), runtime: runtime);
        var preview = (await previewService.CheckAsync(new(), "1.7.1")).LatestRelease!;
        check(UpdatePackagePolicy.SelectableAssets(preview).Select(asset => asset.Name).SequenceEqual(new[] { "ProcessKeeper-v1.8.0-rc.1.exe" }), "preview package filename binds the actual SemVer release instead of accepting another version");
        handler.Requests.Clear();
        try { await service.ProbeAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), release, release.Assets[0]); throw new Exception("ACCEPTED checksum as a downloadable update"); }
        catch (UpdateException error) { check(error.Code == "unsupported-asset", "checksum retained in inventory remains rejected by protected download revalidation"); }
        check(handler.Requests.Count == 1 && handler.Requests[0].AbsoluteUri == "https://api.github.com/repos/KangQiovo/ProcessKeeper/releases/tags/v1.8.0",
            "checksum rejection rechecks official metadata without contacting a binary transport");
    }
    private sealed class Handler(string[] names, string version = "1.8.0") : HttpMessageHandler
    {
        internal readonly List<Uri> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!; Requests.Add(uri);
            if (uri.Host != "api.github.com") throw new InvalidOperationException("Fixture must never contact a binary transport");
            var release = new
            {
                tag_name = "v" + version, name = "Process Keeper " + version, draft = false, prerelease = version.Contains('-'),
                published_at = "2026-10-08T00:00:00Z", body = "Fixture notes", html_url = "https://github.com/KangQiovo/ProcessKeeper/releases/tag/v" + Uri.EscapeDataString(version),
                assets = names.Select((name, index) => new { id = index + 1, name, size = 8192, state = "uploaded", digest = "sha256:" + new string('a', 64),
                    browser_download_url = "https://github.com/KangQiovo/ProcessKeeper/releases/download/v" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(name) }).ToArray()
            };
            object payload = uri.AbsolutePath.Contains("/tags/") ? release : new[] { release };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") });
        }
    }
    private static int IndexOf(this IReadOnlyList<UpdateAsset> values, UpdateAsset value)
    { for (var index = 0; index < values.Count; index++) if (ReferenceEquals(values[index], value)) return index; return -1; }
}
