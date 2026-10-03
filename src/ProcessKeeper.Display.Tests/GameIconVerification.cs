using System.Text.Json;
using ProcessKeeper.Core;

internal static class GameIconVerification
{
    internal static void Run(Action<bool,string> check)
    {
        const string steamRoot = @"E:\Games\Steam";
        const string gameRoot = @"E:\Library\steamapps\common\Original";
        const string cachedIcon = @"E:\Games\Steam\steam\games\original.ico";
        const string main = gameRoot + @"\bin\Original.exe";
        var game = new GameCatalogEntry("123", "Original", gameRoot, GamePlatformCatalog.Steam) { IconPath = cachedIcon };
        string Parse(string key = "Steam App 123", string icon = cachedIcon, string location = gameRoot) =>
            GamePlatformCatalog.ReadSteamIconRegistration(game, new[] { steamRoot }, key, icon, location);
        check(Parse() == cachedIcon, "exact Steam manifest app ID retains its original cached .ico");
        check(Parse(location: "") == cachedIcon, "Steam game icon does not require a guessed installation location");
        check(Parse(icon: "\"" + cachedIcon + "\",0") == cachedIcon, "quoted registered icon with zero resource index retained");
        check(Parse(icon: main) == main, "registered game executable inside its verified installation can supply original icon");
        check(Parse(icon: gameRoot + @"\icon.ico") == gameRoot + @"\icon.ico", "game-local registered .ico retained");
        foreach (var key in new[] { "Steam App 124", "Steam App 0123", "Steam App 123 extra", "Other 123", "" })
            check(Parse(key: key) == "", "unrelated or noncanonical Steam registration cannot change child icon");
        foreach (var icon in new[] { steamRoot + @"\steam.exe", @"E:\Other\icon.ico", cachedIcon + ":stream", @"\\host\games\icon.ico",
            steamRoot + @"\steam\games-copy\icon.ico", steamRoot + @"\steam\games\..\original.ico", "relative.ico", cachedIcon + ",-1",
            cachedIcon + ",2", "https://example.test/icon.ico", @"E:\Library\icon.dll", main + " --argument", cachedIcon + "\n" })
            check(Parse(icon: icon) == "", "unsafe, unsupported or launcher icon evidence stays absent");
        check(Parse(location: @"E:\Other\Original") == "", "conflicting Steam install location cannot claim a cache icon");
        check(Parse(location: "E:\\") == "", "broad declared Steam install location does not claim original icon");
        check(GamePlatformCatalog.ReadSteamIconRegistration(game with { Id = "0123" }, new[] { steamRoot }, "Steam App 0123", cachedIcon, gameRoot) == "", "noncanonical manifest ID rejected by icon parser");
        check(GamePlatformCatalog.ReadSteamIconRegistration(game with { Platform = GamePlatformCatalog.Ea }, new[] { steamRoot }, "Steam App 123", cachedIcon, gameRoot) == "", "Steam cache never becomes another platform's game icon");

        var epicJson = JsonSerializer.Serialize(new { AppName = "epic", DisplayName = "Epic Game", InstallLocation = @"E:\Epic\Example", LaunchExecutable = @"bin\EpicGame.exe", bIsIncompleteInstall = false });
        var epic = GamePlatformCatalog.ReadEpicManifest(epicJson)!;
        check(epic.IconPath == @"E:\Epic\Example\bin\EpicGame.exe" && epic.ExecutablePath == epic.IconPath, "Epic declared launch entry survives catalog as original game icon evidence");

        ProcessRecord Process(string path, int id = 1) => new() { Path = path, Name = Path.GetFileName(path), Id = id };
        ApplicationGroup Running(params string[] paths) => new() { Key = "original", Name = "Original", Processes = paths.Select((path, index) => Process(path, index + 1)).ToArray() };
        InstalledApplication Installed(string path) => new() { Id = path, Name = "Original", InstallLocation = Path.GetDirectoryName(path)!, Executables = new[] { new InstalledExecutable { Path = path } } };
        var catalog = new ApplicationDisplayCatalog(new[] { game, epic }, new[] { new GamePlatformClient(GamePlatformCatalog.Steam, steamRoot + @"\steam.exe") }, Array.Empty<string>());
        var running = Running(main); var before = running.Processes;
        check(catalog.ResolveIconPath(running) == cachedIcon, "running grouped game child uses original registered game icon");
        check(catalog.ResolveIconPath(Installed(main)) == cachedIcon, "installed grouped game child uses original registered game icon");
        var uninstall = new UninstallEntry { Name = "Original", InstallLocation = gameRoot, IconPath = steamRoot + @"\steam.exe", Command = new(steamRoot + @"\steam.exe", "steam://uninstall/123", false) };
        check(catalog.ResolveIconPath(uninstall) == cachedIcon, "Steam uninstall host cannot replace grouped game's original icon");
        check(catalog.ResolveGameIconPath(main) == cachedIcon, "whitelist parent resolves same game original icon");
        check(ReferenceEquals(before, running.Processes) && running.Processes[0].Path == main, "parent icon enrichment preserves original process identity and child executable icon path");
        check(catalog.ResolveIconPath(Running(steamRoot + @"\steam.exe")) == steamRoot + @"\steam.exe", "actual platform client keeps its own launcher icon");
        var missing = new ApplicationDisplayCatalog(new[] { game with { IconPath = "" } }, catalog.Clients, Array.Empty<string>());
        check(missing.ResolveIconPath(uninstall) == "", "unidentified Steam game icon falls back to generic rather than launcher");
        check(missing.ResolveIconPath(uninstall with { ApplicationPaths = new[] { main } }) == main, "game main executable is local fallback when original cache evidence missing");
        check(missing.ResolveIconPath(running) == main, "running game falls back to actual game process icon");
        var malicious = new ApplicationDisplayCatalog(new[] { game with { IconPath = steamRoot + @"\steam.exe" } }, catalog.Clients, Array.Empty<string>());
        check(malicious.ResolveIconPath(uninstall) == "", "even declared game icon cannot borrow registered client icon");
        check(!catalog.HasSamePresentationAs(missing), "changed original icon evidence refreshes presentation");

        foreach (var platform in new[] { GamePlatformCatalog.Steam, GamePlatformCatalog.Epic, GamePlatformCatalog.Ubisoft, GamePlatformCatalog.Ea })
        {
            var root = @"E:\PlatformGames\" + platform.Id;
            var first = new GameCatalogEntry("first", "First", root + @"\First", platform) { IconPath = root + @"\First\First.exe" };
            var second = new GameCatalogEntry("second", "Second", root + @"\Second", platform) { IconPath = root + @"\Second\Second.exe" };
            var client = root + @"\Launcher\Launcher.exe";
            var multi = new ApplicationDisplayCatalog(new[] { first, second }, new[] { new GamePlatformClient(platform, client) }, Array.Empty<string>());
            check(multi.FindGamePlatform(Running(first.IconPath)) == platform && multi.FindGamePlatform(Running(second.IconPath)) == platform, "two original game children share platform display only " + platform.Id);
            check(multi.ResolveIconPath(Running(first.IconPath)) == first.IconPath && multi.ResolveIconPath(Running(second.IconPath)) == second.IconPath, "grouped game children retain distinct original icons " + platform.Id);
            check(multi.ResolveIconPath(Installed(first.IconPath)) == first.IconPath && multi.ResolveIconPath(Installed(second.IconPath)) == second.IconPath, "installed game children never inherit sibling/launcher icon " + platform.Id);
        }

        var diagnostic = @"E:\Apps\Original\a_reporter.exe";
        var mainApp = new InstalledApplication { Id = "main", Name = "Original", EntryPaths = new[] { @"E:\Apps\Original\Original.exe" },
            Executables = new[] { new InstalledExecutable { Path = diagnostic }, new InstalledExecutable { Path = @"E:\Apps\Original\Original.exe" }, new InstalledExecutable { Path = @"E:\Apps\Original\UnInstall.exe" } } };
        check(ApplicationDisplayCatalog.Empty.ResolveIconPath(mainApp) == mainApp.EntryPaths[0], "installed parent chooses unique explicit main icon before alphabetically first diagnostic");
        check(ApplicationDisplayCatalog.Empty.ResolveIconPath(mainApp with { EntryPaths = new[] { diagnostic } }) == mainApp.Executables[1].Path, "diagnostic entry cannot override uniquely inferred main icon");
        check(ApplicationDisplayCatalog.Empty.ResolveIconPath(mainApp with { Name = "Unknown", EntryPaths = Array.Empty<string>() }) == "", "ambiguous multi-executable parent stays generic rather than misleading first file");
        var conflicting = new ApplicationDisplayCatalog(new[] { game, game with { Id = "456" } }, catalog.Clients, Array.Empty<string>());
        check(conflicting.FindGame(running) is null && conflicting.ResolveIconPath(running) == main, "ambiguous game metadata cannot substitute an original icon");
    }
}
