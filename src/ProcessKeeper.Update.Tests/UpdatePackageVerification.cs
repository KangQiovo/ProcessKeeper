using System.Net;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class UpdatePackageVerification
{
    internal static async Task Run(Action<bool, string> check)
    {
        var currentSplit = new UpdateRuntimeIdentity(10, 0, 26100, 0x8664, false, PackageFlavor: UpdatePackageTarget.Windows10x64);
        check(UpdatePackagePolicy.Supports(UpdatePackageTarget.Universal, currentSplit), "a compatible Universal package stays manually available while running a split package");
        using var service = new UpdateService(new Handler());
        var release = (await service.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
        check(release.Assets.Count(asset => asset.CanAutoInstall) > 1, "same release retains host-compatible manual package alternatives");
        var cases = new (UpdateRuntimeIdentity Runtime, UpdatePackageTarget Target, int Count)[]
        {
            (new(6, 1, 7601, 0x14c, true), UpdatePackageTarget.Windows7Compat, 4),
            (new(6, 1, 7600, 0x8664, true, 0), UpdatePackageTarget.Unsupported, 0),
            (new(6, 2, 9200, 0x8664, true), UpdatePackageTarget.Unsupported, 0),
            (new(6, 3, 9600, 0x8664, true), UpdatePackageTarget.Windows7Compat, 4),
            (new(10, 0, 18363, 0x8664, false), UpdatePackageTarget.Windows7Compat, 5),
            (new(10, 0, 19041, 0x8664, false), UpdatePackageTarget.Windows10x64, 5),
            (new(10, 0, 26100, 0x8664, true), UpdatePackageTarget.Windows7Compat, 5),
            (new(10, 0, 26100, 0x14c, false), UpdatePackageTarget.Windows7Compat, 5),
            (new(10, 0, 26100, 0xaa64, false), UpdatePackageTarget.Windows10arm64, 3),
            (new(10, 0, 26100, 0xaa64, true), UpdatePackageTarget.Unsupported, 3),
            (new(10, 0, 18363, 0xaa64, false), UpdatePackageTarget.Unsupported, 0),
            (new(10, 0, 26100, 0x1c4, false), UpdatePackageTarget.Unsupported, 0)
        };
        foreach (var fixture in cases)
        {
            check(UpdatePackagePolicy.Target(fixture.Runtime) == fixture.Target, "runtime package target retains native architecture, UI choice and minimum OS");
            using var targetService = new UpdateService(new Handler(), runtime: fixture.Runtime);
            var targeted = (await targetService.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
            check(targeted.Assets.Count(asset => asset.CanAutoInstall) == fixture.Count, "manual package eligibility follows actual bundle and installer host routes");
        }
        foreach (var host in new[] { new UpdateRuntimeIdentity(6, 1, 7601, 0x14c, true), new UpdateRuntimeIdentity(10, 0, 26100, 0x14c, true), new UpdateRuntimeIdentity(10, 0, 26100, 0x8664, false), new UpdateRuntimeIdentity(10, 0, 26100, 0xaa64, false) })
        {
            foreach (var flavor in new[] { UpdatePackageTarget.Universal, UpdatePackageTarget.Windows7Compat, UpdatePackageTarget.Windows10x64, UpdatePackageTarget.Windows10arm64 })
            {
                var runtime = host with { PackageFlavor = flavor };
                using var targetService = new UpdateService(new Handler(), runtime: runtime);
                var targeted = (await targetService.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
                var selected = UpdatePackagePolicy.DefaultAsset(targeted, runtime);
                check(selected is null ? !UpdatePackagePolicy.Supports(flavor, runtime) : selected.PackageTarget == flavor && selected.DistributionKind == UpdateDistributionKind.Portable,
                    "trusted Universal and split portable flavors default to their exact current package");
                if (selected is not null) check(!UpdatePackagePolicy.IsPackageChange(selected, runtime), "matching package selection does not show a package-change warning");
                var installedRuntime = runtime with { DistributionKind = UpdateDistributionKind.Installer };
                var installedDefault = UpdatePackagePolicy.DefaultAsset(targeted, installedRuntime);
                var installedAssetKind = flavor == UpdatePackageTarget.Universal ? UpdateDistributionKind.Portable : UpdateDistributionKind.Installer;
                check(installedDefault is null ? !targeted.Assets.Any(asset => asset.CanAutoInstall && asset.PackageTarget == flavor && asset.DistributionKind == installedAssetKind) :
                    installedDefault.PackageTarget == flavor && installedDefault.DistributionKind == installedAssetKind,
                    "validated installed distributions default to the same installer target without a portable fallback");
            }
        }
        check(UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-arm64.exe", "1.7.0") == UpdatePackageTarget.Windows10arm64 &&
            UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-arm64.exe", "1.7.1") == UpdatePackageTarget.Unsupported &&
            UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-x86.exe", "1.7.0") == UpdatePackageTarget.Unsupported,
            "split filenames bind declared version and exact public architecture suffix");
        var exactRuntime = currentSplit with { DistributionKind = UpdateDistributionKind.Installer };
        using var exactService = new UpdateService(new Handler(), runtime: exactRuntime);
        var exactRelease = (await exactService.CheckAsync(new(), "1.6.0")).LatestRelease!;
        var expectedSetup = exactRelease.Assets.Single(asset => asset.Name == "ProcessKeeper-v1.7.0-win10-x86-x64-setup.exe");
        check(UpdatePackagePolicy.DefaultAsset(exactRelease, exactRuntime) == expectedSetup, "installer default selects the exact setup asset even when portable files appear first");
        check(UpdatePackagePolicy.DefaultAsset(exactRelease with { Assets = exactRelease.Assets.Where(asset => asset != expectedSetup).ToArray() }, exactRuntime) is null,
            "missing matching installer never silently switches distribution");
        check(UpdatePackagePolicy.DefaultAsset(exactRelease with { Assets = new[] { expectedSetup, expectedSetup with { Id = 99 } } }, exactRuntime) is null,
            "ambiguous matching package never guesses a default");
        check(UpdatePackagePolicy.DefaultAsset(exactRelease, exactRuntime with { DistributionKind = UpdateDistributionKind.Unknown }) is null,
            "unknown installation ownership never guesses an automatic default");
        check(UpdatePackagePolicy.DefaultAsset(exactRelease, currentSplit with { PackageFlavor = UpdatePackageTarget.Unsupported }) is null,
            "missing trusted current package flavor never substitutes the host preference as a default");
        check(UpdatePackagePolicy.IsPackageChange(exactRelease.Assets.Single(asset => asset.Name == "ProcessKeeper-v1.7.0.exe"), exactRuntime) &&
            UpdatePackagePolicy.IsPackageChange(expectedSetup with { DistributionKind = UpdateDistributionKind.Portable }, exactRuntime),
            "manual package switching warns for either target or distribution changes");
        check(!UpdatePackagePolicy.Supports(UpdatePackageTarget.Windows10arm64, currentSplit) &&
            !UpdatePackagePolicy.Supports(UpdatePackageTarget.Windows7Compat, currentSplit with { FrameworkRelease = 0 }),
            "manual switching does not bypass native architecture or missing Framework capability");
        check(UpdatePackagePolicy.IdentifyDistribution("ProcessKeeper-v1.7.1+sample.exe-win10-x86-x64-setup.exe", "1.7.1+sample.exe") == UpdateDistributionKind.Installer,
            "installer naming replaces only the executable suffix and preserves valid SemVer metadata");
        var installedUniversal = exactRuntime with { PackageFlavor = UpdatePackageTarget.Universal };
        var universal = exactRelease.Assets.Single(asset => asset.PackageTarget == UpdatePackageTarget.Universal);
        check(UpdatePackagePolicy.DefaultAsset(exactRelease, installedUniversal) == universal && !UpdatePackagePolicy.IsPackageChange(universal, installedUniversal),
            "installed Universal ownership defaults to its Universal portable artifact without repeatedly warning about an invented setup");
        var decodeDistribution = typeof(LauncherContextReader).GetMethod("DecodeDistributionRecord", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var contextId = new string('a', 32); var sourceHash = new string('b', 64);
        bool DistributionAccepted(string[] fields)
        {
            try { return decodeDistribution.Invoke(null, new object[] { fields, contextId, sourceHash }) as string == "Installer"; }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is InvalidDataException) { return false; }
        }
        var distributionRecord = new[] { "PKDIST1", contextId, sourceHash, "Installer" };
        check(DistributionAccepted(distributionRecord), "protected distribution sidecar binds exact current session and original image identity");
        foreach (var changed in new[] { new[] { "PKDIST0", contextId, sourceHash, "Installer" }, new[] { "PKDIST1", new string('c', 32), sourceHash, "Installer" },
            new[] { "PKDIST1", contextId, new string('c', 64), "Installer" }, new[] { "PKDIST1", contextId, sourceHash, "InstalledFromFilename" } })
            check(!DistributionAccepted(changed), "distribution sidecar rejects wrong format, replayed session, changed source or unknown hints");
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var names = new[] { "ProcessKeeper-v1.7.0-win7-x86-compat.exe", "ProcessKeeper-v1.7.0-win10-x86-x64.exe", "ProcessKeeper-v1.7.0-win10-arm64.exe", "ProcessKeeper-v1.7.0.exe", "ProcessKeeper-v1.7.0-win7-x86-compat-setup.exe", "ProcessKeeper-v1.7.0-win10-x86-x64-setup.exe", "ProcessKeeper-v1.7.0-win10-arm64-setup.exe" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new
            {
                tag_name = "v1.7.0", name = "ProcessKeeper 1.7", draft = false, prerelease = false,
                html_url = "https://github.com/KangQiovo/ProcessKeeper/releases/tag/v1.7.0", body = "", published_at = "2026-10-03T00:00:00Z",
                assets = names.Select((name, index) => new { id = index + 1, name, size = 8192, state = "uploaded", digest = "sha256:" + new string('a', 64),
                    browser_download_url = "https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.7.0/" + name }).ToArray()
            } })) });
        }
    }
}
