using ProcessKeeper.Core;

internal static class UninstallDisplayVerification
{
    internal static void Verify(Action<bool, string> check)
    {
        var gameMethod = typeof(ApplicationDisplayCatalog).GetMethod("FindGame", [typeof(UninstallEntry)]);
        var platformMethod = typeof(ApplicationDisplayCatalog).GetMethod("FindGamePlatform", [typeof(UninstallEntry)]);
        var microsoftMethod = typeof(ApplicationDisplayCatalog).GetMethod("IsMicrosoft", [typeof(UninstallEntry)]);
        var captureMethod = typeof(ApplicationDisplayService).GetMethod("Capture", [typeof(ProcessSnapshot), typeof(IReadOnlyList<InstalledApplication>),
            typeof(IReadOnlyList<AutorunEntry>), typeof(IReadOnlyList<UninstallEntry>), typeof(bool), typeof(CancellationToken)]);
        check(gameMethod is not null && platformMethod is not null && microsoftMethod is not null && captureMethod is not null,
            "uninstall display accepts the same platform and publisher evidence as other lists");
        GameCatalogEntry? Game(ApplicationDisplayCatalog c, UninstallEntry e) => (GameCatalogEntry?)gameMethod!.Invoke(c, [e]);
        GamePlatform? Platform(ApplicationDisplayCatalog c, UninstallEntry e) => (GamePlatform?)platformMethod!.Invoke(c, [e]);
        bool Microsoft(ApplicationDisplayCatalog c, UninstallEntry e) => (bool)microsoftMethod!.Invoke(c, [e])!;
        ApplicationDisplayCatalog Capture(ApplicationDisplayService s, UninstallEntry[] e, bool verify, CancellationToken token = default) =>
            (ApplicationDisplayCatalog)captureMethod!.Invoke(s, [ProcessSnapshot.Empty, Array.Empty<InstalledApplication>(), Array.Empty<AutorunEntry>(), e, verify, token])!;
        var platforms = new[] { GamePlatformCatalog.Steam, GamePlatformCatalog.Epic, GamePlatformCatalog.Ubisoft, GamePlatformCatalog.Ea };
        var games = platforms.Select(p => new GameCatalogEntry(p.Id, p.Name + " example", @"E:\Fixtures\Games\" + p.Id, p)).ToArray();
        var client = @"E:\Fixtures\Steam\steam.exe";
        var microsoft = @"E:\Fixtures\Microsoft\application.exe";
        var other = @"E:\Fixtures\Other\application.exe";
        var catalog = new ApplicationDisplayCatalog(games, [new(GamePlatformCatalog.Steam, client)], [microsoft, @"C:\Windows\System32\msiexec.exe"]);
        foreach (var game in games)
        {
            var entry = new UninstallEntry { Name = "Unrelated display name", InstallLocation = game.InstallDirectory,
                Command = new(@"C:\Windows\System32\msiexec.exe", "/x {fixture}", true) };
            check(Game(catalog, entry) == game && Platform(catalog, entry) == game.Platform, "MSI uninstall groups using registered game directory: " + game.Platform.Id);
            check(Platform(catalog, entry with { InstallLocation = "", IconPath = game.InstallDirectory + @"\bin\game.exe" }) == game.Platform,
                "game executable icon path matches platform without file scans: " + game.Platform.Id);
        }
        check(Platform(catalog, new() { Command = new(client, "-uninstall 123", false) }) == GamePlatformCatalog.Steam,
            "registered Steam uninstall command retains platform attribution");
        check(Platform(catalog, new() { Name = "Steam", Publisher = "Valve", InstallLocation = @"E:\Steam", IconPath = other }) is null,
            "platform name alone never groups an unknown application");
        check(Platform(catalog, new() { InstallLocation = games[1].InstallDirectory, Command = new(client, "", false) }) is null,
            "conflicting launcher and game evidence remains ungrouped");
        check(Game(new([games[0], games[0] with { Platform = GamePlatformCatalog.Epic }], [], []), new() { InstallLocation = games[0].InstallDirectory }) is null,
            "ambiguous platform installation cannot silently choose one owner");
        check(Platform(catalog, new() { InstallLocation = games[0].InstallDirectory + "Else" }) is null,
            "sibling directory prefix is not game ownership");
        var verifiedEntry = new UninstallEntry { IconPath = microsoft, Command = new(@"C:\Windows\System32\msiexec.exe", "/x {fixture}", true) };
        check(Microsoft(catalog, verifiedEntry), "verified application icon identifies Microsoft MSI application");
        check(!Microsoft(catalog, new() { Name = "Microsoft App", Publisher = "Microsoft Corporation", IsSystem = true }),
            "unverified Microsoft registry labels remain visible");
        check(!Microsoft(catalog, verifiedEntry with { IconPath = other }), "Microsoft MSI host cannot hide third-party application");
        check(!Microsoft(catalog, verifiedEntry with { Command = new(other, "", false) }), "mixed signer identity remains visible");
        check(!Microsoft(catalog, verifiedEntry with { QuietCommand = new(other, "", false) }), "mixed quiet-route signer identity remains visible without a resolved main application");
        check(!Microsoft(catalog, verifiedEntry with { IconPath = "" }), "MSI command alone never hides application");
        check(!Microsoft(catalog, new() { IconPath = "invalid icon metadata", Command = new(microsoft, "", false) }),
            "a verified uninstaller alone cannot establish main application ownership when icon metadata is unusable");
        check(!Microsoft(catalog, new() { IconPath = microsoft, Command = new(microsoft, "", false) }),
            "an icon fallback to the verified uninstaller cannot establish main application ownership");
        check(Microsoft(catalog, verifiedEntry with { ApplicationPaths = new[] { microsoft }, IconPath = other, Command = new(other, "", false) }),
            "a resolved verified Microsoft main executable establishes ownership independently of its installer icon");
        check(!Microsoft(catalog, verifiedEntry with { ApplicationPaths = new[] { microsoft, other } }),
            "mixed resolved application executable identities remain visible");
        foreach (var path in new[] { @"\\remote\share\app.exe", "https://example.invalid/app.exe", @"E:\Fixtures\Microsoft\application.exe:stream", @"E:\Fixtures\Microsoft\..\Microsoft\application.exe" })
            check(!Microsoft(catalog, verifiedEntry with { IconPath = path }), "unsafe or indirect uninstall evidence remains visible");
        var probe = new PublisherProbe(); probe.Paths[microsoft] = (new(1, 2, 3, 4, 5), true);
        var service = new ApplicationDisplayService(_ => new(games, [new(GamePlatformCatalog.Steam, client)], []), probe);
        var result = Capture(service, [verifiedEntry], false);
        check(probe.IdentityReads == 0 && !Microsoft(result, verifiedEntry), "uninstall grouping skips signature IO until Microsoft filtering is enabled");
        result = Capture(service, [verifiedEntry], true);
        check(Microsoft(result, verifiedEntry) && probe.Verifications == 1, "uninstall-only executable evidence is captured and verified");
        Capture(service, [verifiedEntry, verifiedEntry], true);
        check(probe.Verifications == 1, "duplicate versions share identity-bound publisher cache");
        probe.Paths[microsoft] = (new(1, 9, 3, 4, 5), false);
        check(!Microsoft(Capture(service, [verifiedEntry], true), verifiedEntry), "replaced uninstall application evidence returns to visible");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10000; i++) Platform(catalog, new() { InstallLocation = games[i % games.Length].InstallDirectory });
        check(timer.Elapsed < TimeSpan.FromSeconds(5), "ten thousand uninstall platform lookups use bounded in-memory path matching");
    }
}
