using System.Net;
using System.Text.Json;
using ProcessKeeper.Core;

internal static class UpdatePackageVerification
{
    internal static async Task Run(Action<bool, string> check)
    {
        using var service = new UpdateService(new Handler());
        var release = (await service.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
        check(release.Assets.Count(asset => asset.CanAutoInstall) == 1, "split release exposes only the current runtime package for in-app installation");
        var cases = new (UpdateRuntimeIdentity Runtime, UpdatePackageTarget Target)[]
        {
            (new(6, 1, 7601, 0x14c, true), UpdatePackageTarget.Windows7Compat),
            (new(6, 1, 7600, 0x8664, true, 0), UpdatePackageTarget.Unsupported),
            (new(6, 2, 9200, 0x8664, true), UpdatePackageTarget.Unsupported),
            (new(6, 3, 9600, 0x8664, true), UpdatePackageTarget.Windows7Compat),
            (new(10, 0, 18363, 0x8664, false), UpdatePackageTarget.Windows7Compat),
            (new(10, 0, 19041, 0x8664, false), UpdatePackageTarget.Windows10x64),
            (new(10, 0, 26100, 0x8664, true), UpdatePackageTarget.Windows7Compat),
            (new(10, 0, 26100, 0x14c, false), UpdatePackageTarget.Windows7Compat),
            (new(10, 0, 26100, 0xaa64, false), UpdatePackageTarget.Windows10arm64),
            (new(10, 0, 26100, 0xaa64, true), UpdatePackageTarget.Unsupported),
            (new(10, 0, 18363, 0xaa64, false), UpdatePackageTarget.Unsupported),
            (new(10, 0, 26100, 0x1c4, false), UpdatePackageTarget.Unsupported)
        };
        foreach (var fixture in cases)
        {
            check(UpdatePackagePolicy.Target(fixture.Runtime) == fixture.Target, "runtime package target retains native architecture, UI choice and minimum OS");
            using var targetService = new UpdateService(new Handler(), runtime: fixture.Runtime);
            var targeted = (await targetService.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
            check(targeted.Assets.Count(asset => asset.CanAutoInstall) == (fixture.Target == UpdatePackageTarget.Unsupported ? 0 : 1) &&
                targeted.Assets.Where(asset => asset.CanAutoInstall).All(asset => asset.PackageTarget == fixture.Target), "official split release permits only its exact supported host package");
        }
        foreach (var host in new[] { new UpdateRuntimeIdentity(6, 1, 7601, 0x14c, true), new UpdateRuntimeIdentity(10, 0, 26100, 0x14c, true), new UpdateRuntimeIdentity(10, 0, 26100, 0x8664, false), new UpdateRuntimeIdentity(10, 0, 26100, 0xaa64, false) })
        {
            foreach (var flavor in new[] { UpdatePackageTarget.Universal, UpdatePackageTarget.Windows7Compat, UpdatePackageTarget.Windows10x64, UpdatePackageTarget.Windows10arm64 })
            {
                var runtime = host with { PackageFlavor = flavor };
                using var targetService = new UpdateService(new Handler(), runtime: runtime);
                var targeted = (await targetService.CheckAsync(new UpdatePreferences(SourceMode: UpdateSourceMode.Official), "1.6.0")).LatestRelease!;
                var expected = UpdatePackagePolicy.Target(runtime);
                check(targeted.Assets.Count(asset => asset.CanAutoInstall) == (expected == UpdatePackageTarget.Unsupported ? 0 : 1) && targeted.Assets.Where(asset => asset.CanAutoInstall).All(asset => asset.PackageTarget == flavor),
                    "trusted Universal and split flavors retain only their own portable update across host architectures");
            }
        }
        check(UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-arm64.exe", "1.7.0") == UpdatePackageTarget.Windows10arm64 &&
            UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-arm64.exe", "1.7.1") == UpdatePackageTarget.Unsupported &&
            UpdatePackagePolicy.Identify("ProcessKeeper-v1.7.0-win10-x86.exe", "1.7.0") == UpdatePackageTarget.Unsupported,
            "split filenames bind declared version and exact public architecture suffix");
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
