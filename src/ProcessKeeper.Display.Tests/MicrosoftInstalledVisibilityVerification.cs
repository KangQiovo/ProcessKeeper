using ProcessKeeper.Core;

internal static class MicrosoftInstalledVisibilityVerification
{
    internal static void Run(Action<bool, string> check)
    {
        const string family = "Microsoft.UnnamedProduct_8wekyb3d8bbwe";
        const string fullName = "Microsoft.UnnamedProduct_1.2.3.4_x64__8wekyb3d8bbwe";
        const string root = @"E:\Fixtures\Packages\Microsoft.UnnamedProduct_1.2.3.4_x64__8wekyb3d8bbwe";
        var path = root + @"\Product\main.exe";
        var package = new InstalledApplication
        {
            Id = "package-fixture", Name = "Arbitrary localized extension", Publisher = "Microsoft Corporation",
            ApplicationKey = "package:" + family.ToLowerInvariant(), InstallLocation = root
        };
        InstalledApplication WithEvidence(InstalledApplication app, string ownerFamily = family, string ownerFullName = fullName,
            string ownerRoot = root, string[]? entries = null) => app with
            { MicrosoftPackageEvidence = new(ownerFamily, ownerFullName, ownerRoot, entries ?? Array.Empty<string>()) };

        package = WithEvidence(package);
        check(ApplicationDisplayCatalog.Empty.IsMicrosoft(package), "trusted registered Microsoft package with zero exe hides on first render");
        var withoutEvidence = package with { MicrosoftPackageEvidence = null };
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(withoutEvidence), "Microsoft publisher text alone cannot hide a zero-exe package");
        var executablePackage = WithEvidence(package with { Executables = new[] { new InstalledExecutable { Path = path } } }, entries: new[] { path });
        check(ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage), "trusted package declarations hide before slow signature capture");
        var helper = root + @"\Product\helper.exe";
        var multiPackage = WithEvidence(executablePackage with { Executables = new[] { new InstalledExecutable { Path = path }, new InstalledExecutable { Path = helper } } }, entries: new[] { path, helper });
        check(ApplicationDisplayCatalog.Empty.IsMicrosoft(multiPackage), "multiple exact Microsoft package application declarations share preloaded attribution");
        check(ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage with { Name = "Third localized label", Publisher = "Resource unresolved" }), "package ownership is independent of localized display strings");
        foreach (var architecture in new[] { "x86", "x64", "arm64", "neutral" })
        {
            var architectureFullName = fullName.Replace("_x64_", "_" + architecture + "_");
            var architectureRoot = root.Replace("_x64_", "_" + architecture + "_");
            check(ApplicationDisplayCatalog.Empty.IsMicrosoft(WithEvidence(package with { InstallLocation = architectureRoot }, ownerFullName: architectureFullName, ownerRoot: architectureRoot)),
                "verified Microsoft package ownership supports architecture " + architecture);
        }
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage with { ApplicationKey = "package:contoso_8wekyb3d8bbwe" }), "package evidence cannot transfer to another registered family");
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage with { InstallLocation = @"E:\Fixtures\CopiedPackage" }), "package evidence cannot transfer to a copied installation root");
        var other = @"E:\Fixtures\Other\other.exe";
        var mixedPackage = executablePackage with { Executables = new[] { new InstalledExecutable { Path = path }, new InstalledExecutable { Path = other } } };
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(mixedPackage), "package evidence never absorbs undeclared third-party executable");
        var unknown = new InstalledApplication { Id = "unknown-zero", Name = package.Name, Publisher = package.Publisher };
        var merged = executablePackage with { Id = "merged", Installations = new[] { executablePackage, unknown } };
        var pathsOnly = new ApplicationDisplayCatalog([], [], new[] { path });
        check(!pathsOnly.IsMicrosoft(merged), "mixed merged family preserves an unknown zero-exe original installation");
        check(ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage with { Installations = new[] { executablePackage, package } }), "all trusted package originals hide even when one has zero exe");
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(executablePackage with { Installations = new[] { executablePackage, unknown with { Executables = new[] { new InstalledExecutable { Path = other } } } } }), "merged trusted package and third-party desktop originals remain visible");
        var adobe = new InstalledApplication { Id = "adobe", Name = "UXP WebView Support", Publisher = "Adobe Inc.",
            Executables = new[] { new InstalledExecutable { Path = path } } };
        check(!pathsOnly.IsMicrosoft(adobe), "third-party Adobe application cannot inherit its Microsoft helper publisher");
        check(!pathsOnly.IsMicrosoft(adobe with { Name = "Windows driver package", Publisher = "libusb-win32" }), "third-party driver application cannot inherit its Microsoft helper publisher");
        check(pathsOnly.IsMicrosoft(adobe with { Publisher = "Microsoft Corporation" }), "exact Microsoft desktop publisher still requires and accepts verified executable evidence");
        check(!pathsOnly.IsMicrosoft(new ApplicationGroup { Company = "Adobe Inc.", Processes = new[] { new ProcessRecord { Path = path } } }), "third-party running group cannot inherit its Microsoft helper publisher");
        check(!pathsOnly.IsMicrosoft(new AutorunEntry { SourceKind = AutorunSourceKind.Service, TargetPath = path, Ownership = AutorunOwnership.ThirdParty }), "third-party startup ownership cannot inherit its Microsoft helper signer");
        var sharedRuntime = @"E:\Fixtures\Runtime\dotnet.exe";
        var runtimeCatalog = new ApplicationDisplayCatalog([], [], new[] { sharedRuntime });
        check(!runtimeCatalog.IsMicrosoft(new ApplicationGroup { Company = "Microsoft Corporation", Processes = new[] { new ProcessRecord { Path = sharedRuntime } } }), "shared Microsoft runtime does not hide a hosted third-party running group");
        check(!runtimeCatalog.IsMicrosoft(new AutorunEntry { SourceKind = AutorunSourceKind.RegistryRun, TargetPath = sharedRuntime, Command = "dotnet.exe third-party.dll" }), "shared Microsoft runtime does not hide a hosted third-party startup entry");
        foreach (var alias in new[] { "Microsoft Corporation", "Microsoft", "Microsoft Corp.", "微软公司", "微軟公司", "微软", "微軟" })
            check(pathsOnly.IsMicrosoft(adobe with { Publisher = alias }), "official Microsoft publisher alias permits verified signatures only: " + alias);

        var probe = new PublisherProbe(); probe.Paths[path] = (new(1, 2, 3, 4, 5), false);
        var process = new ProcessRecord { Id = 1, Path = path };
        var service = new ApplicationDisplayService(_ => new([], [], []), probe);
        var captured = service.Capture(new(DateTimeOffset.UtcNow, new[] { process }, []), new[] { executablePackage, package }, []);
        check(captured.IsMicrosoft(process), "registered package exact paths preload running attribution without standalone signature");
        check(probe.Verifications == 0, "registered package preloading does not consume native signature worker budget");
        check(!captured.IsMicrosoft(new ProcessRecord { Path = root + @"\Undeclared.exe" }), "matching trusted package root text cannot attribute an undeclared executable");
        check(!captured.HasSamePresentationAs(service.Capture(ProcessSnapshot.Empty, [], [])), "removing trusted zero-exe package ownership invalidates display presentation");
        var zeroOnly = service.Capture(ProcessSnapshot.Empty, new[] { package }, []);
        check(!zeroOnly.HasSamePresentationAs(ApplicationDisplayCatalog.Empty), "zero-exe package evidence is included in presentation comparison");
        var mergedCapture = service.Capture(ProcessSnapshot.Empty, new[] { executablePackage with { Installations = new[] { executablePackage, package } } }, []);
        check(mergedCapture.IsMicrosoft(process), "capture flattens merged originals for exact package path preloading");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { service.Capture(ProcessSnapshot.Empty, new[] { package }, [], cancelled.Token); check(false, "cancelled package capture must throw"); }
        catch (OperationCanceledException) { check(true, "cancelled package capture stops before preloading metadata"); }

        bool RequiresRefresh(ApplicationDisplayCatalog result) => result.RequiresMicrosoftRefresh;
        using var hold = new ManualResetEventSlim(false);
        var slow = new PublisherProbe { DuringVerification = () => hold.Wait() };
        slow.Paths[other] = (new(1, 10, 3, 4, 5), false);
        var slowService = new ApplicationDisplayService(_ => new([], [], []), slow);
        var snapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, new[] { new ProcessRecord { Path = other } }, []);
        try
        {
            check(RequiresRefresh(slowService.Capture(snapshot, [], [])), "unfinished native publisher capture requests another background refresh");
        }
        finally { hold.Set(); SpinWait.SpinUntil(() => slow.Finished == 1, 3000); }
        check(!RequiresRefresh(slowService.Capture(snapshot, [], [])), "completed negative publisher result stops background refresh requests");
        check(!RequiresRefresh(service.Capture(ProcessSnapshot.Empty, new[] { package }, [])), "trusted zero-exe package does not poll when attribution is complete");

        var candidatePackage = withoutEvidence with { Id = "candidate-zero" };
        var signature = root + @"\AppxSignature.p7x";
        candidatePackage = candidatePackage with { MicrosoftPackageSignatureCandidate = new(family, fullName, root,
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", signature, Array.Empty<string>()) };
        var signedProbe = new PublisherProbe(); signedProbe.Paths[signature] = (new(1, 11, 22, 33, 44), true);
        var signatureService = new ApplicationDisplayService(_ => new([], [], []), signedProbe);
        check(!ApplicationDisplayCatalog.Empty.IsMicrosoft(candidatePackage), "registered non-Store package metadata stays visible before extra package signature verification");
        var signedPackageCapture = signatureService.Capture(ProcessSnapshot.Empty, new[] { candidatePackage }, []);
        check(signedPackageCapture.IsMicrosoft(candidatePackage), "verified package signature hides registered non-Store package with zero exe");
        signedProbe.Paths[signature] = (new(1, 12, 22, 33, 44), false);
        check(!signatureService.Capture(ProcessSnapshot.Empty, new[] { candidatePackage }, []).IsMicrosoft(candidatePackage), "changed non-Store package signature invalidates zero-exe ownership");

        using var cancelledWork = new CancellationTokenSource();
        var cancelProbe = new PublisherProbe { DuringVerification = () => cancelledWork.Cancel() };
        cancelProbe.Paths[other] = (new(1, 91, 2, 3, 4), true);
        var cancelService = new ApplicationDisplayService(_ => new([], [], []), cancelProbe);
        try { cancelService.Capture(snapshot, [], [], cancelledWork.Token); check(false, "in-flight capture must honor cancellation"); }
        catch (OperationCanceledException) { check(true, "in-flight publisher capture propagates cancellation"); }
        cancelProbe.DuringVerification = null;
        check(SpinWait.SpinUntil(() => cancelService.Capture(snapshot, [], []).IsMicrosoft(new ProcessRecord { Path = other }), 3000),
            "cancelled native work cannot poison a later unchanged file with a cached negative publisher");

        var largeProbe = new PublisherProbe();
        var processes = Enumerable.Range(1, 4200).Select(index => new ProcessRecord { Id = index, Path = @"E:\Fixtures\Large\app" + index + ".exe" }).ToArray();
        foreach (var record in processes) largeProbe.Paths[record.Path] = (new(1, (ulong)record.Id, 1, 2, 3), true);
        var largeService = new ApplicationDisplayService(_ => new([], [], []), largeProbe);
        var largeSnapshot = new ProcessSnapshot(DateTimeOffset.UtcNow, processes, []);
        ApplicationDisplayCatalog large = ApplicationDisplayCatalog.Empty;
        for (int pass = 0; pass < 40; pass++)
        {
            large = largeService.Capture(largeSnapshot, [], []);
            if (!large.RequiresMicrosoftRefresh) break;
        }
        check(!large.RequiresMicrosoftRefresh && largeProbe.Verifications == 4200 && processes.All(large.IsMicrosoft),
            "inventory above old cache capacity finishes once without evicting and repeatedly probing earlier signatures");
    }
}
