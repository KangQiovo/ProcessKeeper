using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

public sealed record GamePlatform(string Id, string Name);
public sealed record GameCatalogEntry(string Id, string Name, string InstallDirectory, GamePlatform Platform)
{
    /// <summary>Original registered game icon or manifest entry. Display-only; never launcher, launch or close authority.</summary>
    public string IconPath { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
}
public sealed record GamePlatformClient(GamePlatform Platform, string ExecutablePath);
public sealed record GamePlatformSnapshot(IReadOnlyList<GameCatalogEntry> Games, IReadOnlyList<GamePlatformClient> Clients, IReadOnlyList<string> Warnings);

/// <summary>Read-only launcher installation evidence. No account data, launch actions or process ownership changes.</summary>
public sealed class GamePlatformCatalog
{
    public static GamePlatform Steam { get; } = new("steam", "Steam");
    public static GamePlatform Epic { get; } = new("epic", "Epic Games");
    public static GamePlatform Ubisoft { get; } = new("ubisoft", "Ubisoft Connect");
    public static GamePlatform Ea { get; } = new("ea", "EA app");
    private readonly SteamLibraryCatalog _steam;
    public GamePlatformCatalog(SteamLibraryCatalog? steam = null) => _steam = steam ?? new();

    public GamePlatformSnapshot Scan(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var steam = _steam.Scan(token);
        var games = steam.Games.Select(game => new GameCatalogEntry(game.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture), game.Name, game.InstallDirectory, Steam)).ToList();
        var clients = steam.ClientDirectories.Select(root => new GamePlatformClient(Steam, Path.Combine(root, "steam.exe"))).ToList();
        var warnings = steam.Warnings.ToList(); var clock = Stopwatch.StartNew();
        bool Available() => games.Count < 8192 && clock.Elapsed < TimeSpan.FromSeconds(3);
        // Steam stores each game's original icon separately from steam.exe. Exact app IDs from
        // complete local manifests bind those cache files; no executable or drive-wide discovery.
        for (var index = 0; index < games.Count && Available(); index++)
        {
            token.ThrowIfCancellationRequested(); var game = games[index];
            var icons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
            {
                token.ThrowIfCancellationRequested(); if (!Available()) break;
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var registration = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + game.Id);
                    var icon = ReadSteamIconRegistration(game, steam.ClientDirectories, "Steam App " + game.Id,
                        Value(registration, "DisplayIcon"), Value(registration, "InstallLocation"));
                    if (icon.Length > 0 && File.Exists(icon))
                    { AutorunPathSafety.RejectReparseAncestors(icon); icons.Add(icon); }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* Missing/inaccessible original icon stays generic, never a launcher fallback. */ }
            }
            if (icons.Count == 1) games[index] = game with { IconPath = icons.Single() };
        }
        var epicDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests") };
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            token.ThrowIfCancellationRequested(); if (!Available()) break;
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using (var launcher = root.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher"))
                {
                    var path = Value(launcher, "InstallDir");
                    AddClient(Ubisoft, path, "UbisoftConnect.exe"); AddClient(Ubisoft, path, "upc.exe"); AddClient(Ubisoft, path, "uplay.exe");
                }
                ReadGames(root, @"SOFTWARE\Ubisoft\Launcher\Installs", Ubisoft, ["InstallDir"]);
                ReadGames(root, @"SOFTWARE\EA Games", Ea, ["Install Dir", "InstallDir"]);
                ReadGames(root, @"SOFTWARE\Electronic Arts\EA Games", Ea, ["Install Dir", "InstallDir"]);
                using (var launcher = root.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Desktop"))
                {
                    var path = Value(launcher, "InstallLocation");
                    AddClient(Ea, path, "EADesktop.exe"); AddClient(Ea, path, @"EA Desktop\EADesktop.exe");
                }
                using (var launcher = root.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine"))
                {
                    var path = Value(launcher, "INSTALLDIR");
                    AddEpicClient(path);
                }
                using (var launcher = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Epic Games Launcher"))
                    AddEpicClient(Value(launcher, "InstallLocation"));
            }
            catch (OperationCanceledException) { throw; }
            catch { Warn(); }
        }
        foreach (var directory in epicDirectories.Take(8))
        {
            token.ThrowIfCancellationRequested(); if (!Available()) break;
            try
            {
                if (!Directory.Exists(directory)) continue;
                AutorunPathSafety.RejectReparseAncestors(directory);
                foreach (var path in Directory.EnumerateFiles(directory, "*.item").Take(4096))
                {
                    token.ThrowIfCancellationRequested(); if (!Available()) break;
                    try
                    {
                        var json = SteamLibraryCatalog.ReadText(path, 1024 * 1024, token);
                        var entry = ReadEpicManifest(json);
                        if (entry is null || !Directory.Exists(entry.InstallDirectory)) continue;
                        AutorunPathSafety.RejectReparseAncestors(entry.InstallDirectory);
                        using var document = JsonDocument.Parse(json);
                        var executable = DisplayPath.Normalize(Path.Combine(entry.InstallDirectory, document.RootElement.GetProperty("LaunchExecutable").GetString()!));
                        if (!File.Exists(executable)) continue;
                        AutorunPathSafety.RejectReparseAncestors(executable); games.Add(entry);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { Warn(); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { Warn(); }
        }
        if (!Available()) warnings.Add(L.T("游戏平台读取达到本轮上限，未核实的软件保持原有分组。"));
        return new(Array.AsReadOnly(games.Distinct().Take(8192).ToArray()), Array.AsReadOnly(clients.Distinct().Take(32).ToArray()), Array.AsReadOnly(warnings.Distinct().Take(40).ToArray()));

        void Warn() { if (warnings.Count < 40) warnings.Add(L.T("部分游戏平台安装记录无法核实，保持原有分组。")); }
        void AddClient(GamePlatform platform, string? path, string relative)
        {
            var folder = DisplayPath.Normalize(path); if (folder.Length == 0 || clients.Count >= 32) return;
            var executable = DisplayPath.Normalize(Path.Combine(folder, relative));
            if (!File.Exists(executable)) return;
            AutorunPathSafety.RejectReparseAncestors(executable); clients.Add(new(platform, executable));
        }
        void AddEpicClient(string? path)
        {
            var folder = DisplayPath.Normalize(path); if (folder.Length == 0) return;
            AddClient(Epic, folder, @"Portal\Binaries\Win64\EpicGamesLauncher.exe");
            AddClient(Epic, folder, @"Portal\Binaries\Win32\EpicGamesLauncher.exe");
            if (epicDirectories.Count < 8) epicDirectories.Add(Path.Combine(folder, @"Epic\EpicGamesLauncher\Data\Manifests"));
        }
        void ReadGames(RegistryKey root, string location, GamePlatform platform, string[] valueNames)
        {
            using var container = root.OpenSubKey(location); if (container is null) return;
            // Counts are checked before allocating the registry API's names array.
            if (container.SubKeyCount > 4096) { Warn(); return; }
            foreach (var id in container.GetSubKeyNames().Take(4096))
            {
                token.ThrowIfCancellationRequested(); if (!Available()) break;
                if (platform == Ubisoft && !uint.TryParse(id, out _)) continue;
                using var key = container.OpenSubKey(id);
                var path = valueNames.Select(name => Value(key, name)).FirstOrDefault(value => value.Length > 0) ?? "";
                var entry = ReadRegisteredGame(platform, id, Value(key, "DisplayName"), path);
                if (entry is null || !Directory.Exists(entry.InstallDirectory)) continue;
                AutorunPathSafety.RejectReparseAncestors(entry.InstallDirectory);
                var icon = ReadGameIconValue(Value(key, "DisplayIcon"));
                if (icon.Length > 0 && DisplayPath.Under(icon, entry.InstallDirectory) && File.Exists(icon))
                { AutorunPathSafety.RejectReparseAncestors(icon); entry = entry with { IconPath = icon }; }
                games.Add(entry);
            }
        }
    }

    public static GameCatalogEntry? ReadRegisteredGame(GamePlatform platform, string id, string name, string path)
    {
        if (platform != Ubisoft && platform != Ea || !ValidText(id, 255)) return null;
        if (platform == Ubisoft && !uint.TryParse(id, out _)) return null;
        var directory = DisplayPath.Normalize(path);
        if (!SpecificDirectory(directory)) return null;
        var title = ValidText(name, 512) ? name : Path.GetFileName(directory);
        return new(id, title, directory, platform);
    }

    public static GameCatalogEntry? ReadEpicManifest(string json)
    {
        if (json is null || json.Length > 1024 * 1024) throw new InvalidDataException();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1)) return null;
        string Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        var id = Get("AppName"); var name = Get("DisplayName"); var directory = DisplayPath.Normalize(Get("InstallLocation")); var launch = Get("LaunchExecutable");
        if (!ValidText(id, 255) || !ValidText(name, 512) || !SpecificDirectory(directory) || string.IsNullOrWhiteSpace(launch) ||
            Path.IsPathRooted(launch) || launch.Split('\\', '/').Any(part => part is "." or "..") || launch.IndexOf(':') >= 0 ||
            !root.TryGetProperty("bIsIncompleteInstall", out var incomplete) || incomplete.ValueKind != JsonValueKind.False) return null;
        var executable = DisplayPath.Normalize(Path.Combine(directory, launch));
        return executable.Length == 0 || !DisplayPath.Under(executable, directory) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null :
            new GameCatalogEntry(id, name, directory, Epic) { IconPath = executable, ExecutablePath = executable };
    }
    /// <summary>Pure parsing of original icon evidence. The scanner separately checks file existence and reparse safety.</summary>
    public static string ReadSteamIconRegistration(GameCatalogEntry game, IReadOnlyList<string> clientDirectories,
        string registrationName, string displayIcon, string installLocation)
    {
        if (game.Platform != Steam || !uint.TryParse(game.Id, out var id) || id == 0 ||
            game.Id != id.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !registrationName.Equals("Steam App " + game.Id, StringComparison.OrdinalIgnoreCase)) return "";
        var directory = DisplayPath.Normalize(game.InstallDirectory);
        if (directory.Length == 0 || installLocation.Length > 0 &&
            !DisplayPath.Normalize(installLocation).Equals(directory, StringComparison.OrdinalIgnoreCase)) return "";
        var icon = ReadGameIconValue(displayIcon);
        if (icon.Length == 0) return "";
        if (DisplayPath.Under(icon, directory)) return icon;
        // Only .ico cache entries are allowed outside the game. steam.exe is never a game's icon.
        return icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && clientDirectories.Take(16).Any(root =>
            DisplayPath.Normalize(root) is { Length: > 0 } normalized && DisplayPath.Under(icon, Path.Combine(normalized, @"steam\games"))) ? icon : "";
    }
    private static string ReadGameIconValue(string value)
    {
        if (value is null || value.Length > 32767 || value.Any(char.IsControl)) return "";
        var path = value.Trim(); var comma = path.LastIndexOf(',');
        // Current loaders read the file's default icon. A nonzero resource index cannot be guessed.
        if (comma >= 0 && int.TryParse(path.Substring(comma + 1).Trim(), out var index))
        { if (index != 0) return ""; path = path.Substring(0, comma).Trim(); }
        if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"') path = path.Substring(1, path.Length - 2);
        if (path.Split('\\', '/').Any(part => part is "." or "..")) return "";
        path = DisplayPath.Normalize(path);
        return path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : "";
    }
    private static string Value(RegistryKey? key, string name) => InstallationRegistryText.Read(key, name);
    private static bool ValidText(string value, int max) => value.Length > 0 && value.Length <= max && !value.Any(char.IsControl);
    private static bool SpecificDirectory(string directory) => directory.Length > 3 && directory.Split('\\').Length >= 3 &&
        !new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }
            .Any(root => directory.Equals(DisplayPath.Normalize(root), StringComparison.OrdinalIgnoreCase));
}
