namespace ProcessKeeper.Core;

public sealed partial class ApplicationDisplayCatalog
{
    public GameCatalogEntry? FindGame(UninstallEntry entry) => UninstallAttribution(entry).Game;
    public GamePlatform? FindGamePlatform(UninstallEntry entry) => UninstallAttribution(entry).Platform;

    // Registry publisher/system labels and a Microsoft-signed shared installer are not ownership evidence.
    public bool IsMicrosoft(UninstallEntry entry)
    {
        if (entry.ApplicationPaths.Count > 0) return AllMicrosoft(entry.ApplicationPaths);
        // DisplayIcon may be a fallback to the uninstall route. An installer cannot establish application ownership.
        if (entry.Command?.Executable.Equals(entry.IconPath, StringComparison.OrdinalIgnoreCase) == true ||
            entry.QuietCommand?.Executable.Equals(entry.IconPath, StringComparison.OrdinalIgnoreCase) == true) return false;
        var evidence = UninstallEvidencePaths(entry);
        // Without an associated main executable, conflicting app-specific paths keep the entry visible.
        return evidence.Any(path => path.Equals(entry.IconPath, StringComparison.OrdinalIgnoreCase)) && AllMicrosoft(evidence);
    }

    internal static IReadOnlyList<string> UninstallEvidencePaths(UninstallEntry entry)
    {
        var paths = entry.ApplicationPaths.Concat(new[] { entry.IconPath, entry.Command is { IsMsi: false } command ? command.Executable : "",
            entry.QuietCommand is { IsMsi: false } quiet ? quiet.Executable : "" });
        return paths.Where(path => UninstallPolicy.IsLocalExecutable(path) && !SharedHosts.Contains(Path.GetFileName(path))
            && !Path.GetFileName(path).Equals("control.exe", StringComparison.OrdinalIgnoreCase)
            && !Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private (GameCatalogEntry? Game, GamePlatform? Platform) UninstallAttribution(UninstallEntry entry)
    {
        var paths = UninstallEvidencePaths(entry).ToList();
        var location = entry.InstallLocation;
        if (location.Length < 32700 && !location.Split('\\', '/').Any(part => part is "." or ".."))
        {
            var root = DisplayPath.Normalize(location);
            // An in-memory sentinel lets the existing indexed ancestor lookup include the install root itself.
            // No directory enumeration or file creation is involved.
            if (root.Length > 3) paths.Add(root + @"\__processkeeper_display__.exe");
        }
        GameCatalogEntry? game = null; GamePlatform? platform = null;
        foreach (var path in paths)
        {
            var match = FindGamePath(path, out var ambiguous);
            if (ambiguous || match is not null && game is not null && match != game) return (null, null);
            var owner = match?.Platform ?? FindClient(path);
            if (owner is not null && platform is not null && owner != platform) return (null, null);
            game ??= match; platform ??= owner;
        }
        return (game, platform);
    }
}
