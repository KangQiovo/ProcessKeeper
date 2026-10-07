using System.Reflection;
using ProcessKeeper.Core;

internal static class MicrosoftPackageVerification
{
    private const string MicrosoftPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    internal static void Run(Action<bool, string> check, string[] args)
    {
        var method = typeof(MicrosoftPublisherProbe).GetMethod("HasTrustedMicrosoftPackagePublisher", BindingFlags.Static | BindingFlags.NonPublic);
        bool Trusted(string publisher, string id, int kind, bool healthy) =>
            method?.Invoke(null, new object[] { publisher, id, kind, healthy }) is true;
        // These are the OS registration values observed for Microsoft.Paint, including Store signing.
        check(Trusted(MicrosoftPublisher, "8wekyb3d8bbwe", 3, true), "healthy Store-signed Paint uses registered Microsoft publisher evidence");
        check(Trusted(MicrosoftPublisher, "8wekyb3d8bbwe", 4, true), "healthy system-signed Microsoft package uses registered publisher evidence");
        foreach (var kind in new[] { 0, 1, 2, -1, 5 })
            check(!Trusted(MicrosoftPublisher, "8wekyb3d8bbwe", kind, true), "unsigned, developer, enterprise or unknown package signing stays visible: " + kind);
        check(!Trusted(MicrosoftPublisher, "8wekyb3d8bbwe", 3, false), "unhealthy Microsoft Store package stays visible");
        foreach (var publisher in new[] { "CN=Microsoft Corporation, O=Contoso", "CN=Paint, O=Microsoft Corporation Evil", "CN=Paint, O=Microsoft", "CN=Paint, O=Microsoft Corporation, O=Contoso", "Microsoft Corporation" })
            check(!Trusted(publisher, "8wekyb3d8bbwe", 3, true), "package publisher lookalike never becomes Microsoft ownership");
        foreach (var id in new[] { "", "8wekyb3d8bbwex", "contosopublisher", "8WEKYB3D8BBWE" })
            check(!Trusted(MicrosoftPublisher, id, 3, true), "package publisher ID must be the exact registered Microsoft identity");

        const string path = @"E:\Fixtures\Microsoft.Paint_11.2605.81.0_x64__8wekyb3d8bbwe\PaintApp\mspaint.exe";
        var probe = new PublisherProbe(); probe.Paths[path] = (new(1, 4, 5, 6, 7), true);
        var installed = new InstalledApplication { Name = "画图", Publisher = "Microsoft Corporation", ApplicationKey = "package:microsoft.paint_8wekyb3d8bbwe", Executables = new[] { new InstalledExecutable { Path = path } } };
        var process = new ProcessRecord { Id = 8, Path = path, Name = "mspaint.exe", PackageFamilyName = "Microsoft.Paint_8wekyb3d8bbwe" };
        var group = new ApplicationGroup { Processes = new[] { process } };
        var startup = new AutorunEntry { SourceKind = AutorunSourceKind.PackagedStartup, TargetPath = path };
        var uninstall = new UninstallEntry { ApplicationPaths = new[] { path } };
        var service = new ApplicationDisplayService(_ => new([], [], []), probe);
        var result = service.Capture(new(DateTimeOffset.UtcNow, new[] { process }, new[] { group }), new[] { installed }, new[] { startup }, new[] { uninstall }, true);
        check(result.IsMicrosoft(installed) && result.IsMicrosoft(group) && result.IsMicrosoft(process) && result.IsMicrosoft(uninstall), "verified Paint ownership is shared across installed, running and uninstall lists");
        check(result.IsMicrosoft(startup), "direct packaged startup uses the same verified executable ownership");
        check(!result.IsMicrosoft(startup with { TargetPath = @"E:\Fixtures\Microsoft.Paint_11.2605.81.0_x64__8wekyb3d8bbwe\unverified.exe" }), "matching package folder text cannot hide an unverified startup");
        check(!result.IsMicrosoft(installed with { Executables = new[] { new InstalledExecutable { Path = path }, new InstalledExecutable { Path = @"E:\Fixtures\Other\other.exe" } } }), "mixed Microsoft and third-party installed components remain visible");

        if (args.Length == 2 && args[0] == "--registered-paint-path")
        {
            var native = new MicrosoftPublisherProbe();
            check(native.ReadIdentity(args[1]) is not null && native.IsMicrosoft(args[1], default), "real registered Paint executable is Microsoft despite missing standalone file-signature attribution");
            var nativeService = new ApplicationDisplayService(_ => new([], [], []), native);
            var nativeInstalled = installed with { Executables = new[] { new InstalledExecutable { Path = args[1] } } };
            var nativeProcess = process with { Path = args[1] };
            var nativeGroup = group with { Processes = new[] { nativeProcess } };
            var nativeStartup = startup with { TargetPath = args[1] };
            var nativeUninstall = uninstall with { ApplicationPaths = new[] { args[1] } };
            var nativeSnapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, new[] { nativeProcess }, new[] { nativeGroup });
            ApplicationDisplayCatalog captured = ApplicationDisplayCatalog.Empty;
            check(SpinWait.SpinUntil(() =>
            {
                captured = nativeService.Capture(nativeSnapshot, new[] { nativeInstalled }, new[] { nativeStartup }, new[] { nativeUninstall }, true);
                return captured.IsMicrosoft(nativeInstalled);
            }, 5000) && captured.IsMicrosoft(nativeGroup) && captured.IsMicrosoft(nativeStartup) && captured.IsMicrosoft(nativeUninstall),
                "real Paint package attribution reaches every application-list filter through bounded background capture");
            var fixture = Path.Combine(Path.GetTempPath(), "ProcessKeeper-Paint-copy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            try
            {
                var copy = Path.Combine(fixture, "Microsoft.Paint_11.2605.81.0_x64__8wekyb3d8bbwe", "PaintApp", "mspaint.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(args[1], copy);
                check(!native.IsMicrosoft(copy, default), "copied Paint payload outside its registered package cannot inherit package trust");
            }
            finally { Directory.Delete(fixture, true); }
        }
    }
}
