namespace ProcessKeeper.Core;

public sealed partial class ApplicationDisplayCatalog
{
    /// <summary>Display-only icon identity. Platform grouping never substitutes the launcher's icon for its game children.</summary>
    public string ResolveIconPath(ApplicationGroup application)
    {
        var game = FindGame(application);
        var original = OriginalGameIcon(game);
        if (original.Length > 0) return original;
        return application.Processes.OrderByDescending(process => process.HasVisibleWindow)
            .ThenBy(process => process.Id).Select(process => DisplayPath.Normalize(process.Path))
            .FirstOrDefault(path => path.Length > 0 && (game is null || DisplayPath.Under(path, game.InstallDirectory))) ?? "";
    }

    public string ResolveIconPath(InstalledApplication application, InstalledExecutableIdentity? identities = null)
    {
        var game = FindGame(application);
        var original = OriginalGameIcon(game);
        if (original.Length > 0) return original;
        var main = (identities ?? new InstalledExecutableIdentity()).ResolveMain(application);
        if (main.Status == InstalledMainIdentityStatus.Resolved) return main.ExecutablePath;
        // A folder can contain helpers, reporters and uninstallers. Alphabetical order proves no main identity.
        return application.Executables.Count == 1 ? application.Executables[0].Path : "";
    }

    public string ResolveIconPath(UninstallEntry entry)
    {
        var game = FindGame(entry);
        var original = OriginalGameIcon(game);
        if (original.Length > 0) return original;
        if (game is not null)
        {
            // Steam's uninstall route is steam.exe; that route may group the game, but cannot become its icon.
            return entry.ApplicationPaths.Concat(new[] { entry.IconPath }).Select(DisplayPath.Normalize)
                .FirstOrDefault(path => path.Length > 0 && DisplayPath.Under(path, game.InstallDirectory)) ?? "";
        }
        return entry.ApplicationIdentity is { IsResolved: true } identity ? identity.ExecutablePath :
            entry.ApplicationPaths.Count == 1 ? entry.ApplicationPaths[0] : entry.IconPath;
    }

    /// <summary>Use for an application's/rule's parent row only. A process or executable child keeps its own file icon.</summary>
    public string ResolveGameIconPath(string executablePath)
    {
        var original = OriginalGameIcon(FindGamePath(executablePath));
        return original.Length > 0 ? original : executablePath;
    }

    private string OriginalGameIcon(GameCatalogEntry? game)
    {
        if (game is null) return "";
        var path = DisplayPath.Normalize(game.IconPath);
        if (path.Length > 0 && FindClient(path) is null) return path;
        path = DisplayPath.Normalize(game.ExecutablePath);
        return path.Length > 0 && DisplayPath.Under(path, game.InstallDirectory) && FindClient(path) is null ? path : "";
    }
}
