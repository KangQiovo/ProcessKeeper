using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace ProcessKeeper.Core;

public sealed record GamePlatform(string Id, string Name);
public sealed record GameCatalogEntry(string Id, string Name, string InstallDirectory, GamePlatform Platform);
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
                AutorunPathSafety.RejectReparseAncestors(entry.InstallDirectory); games.Add(entry);
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
        return executable.Length == 0 || !DisplayPath.Under(executable, directory) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : new(id, name, directory, Epic);
    }
    private static string Value(RegistryKey? key, string name) => InstallationRegistryText.Read(key, name);
    private static bool ValidText(string value, int max) => value.Length > 0 && value.Length <= max && !value.Any(char.IsControl);
    private static bool SpecificDirectory(string directory) => directory.Length > 3 && directory.Split('\\').Length >= 3 &&
        !new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) }
            .Any(root => directory.Equals(DisplayPath.Normalize(root), StringComparison.OrdinalIgnoreCase));
}
