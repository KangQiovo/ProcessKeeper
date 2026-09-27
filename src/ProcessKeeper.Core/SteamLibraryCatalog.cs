using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Steam's installation metadata is display evidence only, never process protection or close authority.</summary>
public sealed record SteamGame(uint AppId, string Name, string InstallDirectory, string LibraryDirectory);

public sealed class SteamLibraryIndex
{
    public static SteamLibraryIndex Empty { get; } = new([], [], []);
    public IReadOnlyList<SteamGame> Games { get; }
    public IReadOnlyList<string> ClientDirectories { get; }
    public IReadOnlyList<string> Warnings { get; }
    public SteamLibraryIndex(IEnumerable<SteamGame> games, IEnumerable<string> clients, IEnumerable<string> warnings)
    { Games = Array.AsReadOnly(games.Take(4096).ToArray()); ClientDirectories = Array.AsReadOnly(clients.Take(16).ToArray()); Warnings = Array.AsReadOnly(warnings.Take(40).ToArray()); }
    public SteamGame? Find(string path)
    {
        var normalized = DisplayPath.Normalize(path);
        if (normalized.Length == 0) return null;
        // Overlapping or conflicting manifests are ambiguous, even if one path is more specific.
        var matches = Games.Where(game => DisplayPath.Under(normalized, game.InstallDirectory)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public bool IsClientPath(string path)
    {
        var normalized = DisplayPath.Normalize(path);
        if (normalized.Length == 0 || normalized.Split('\\').Any(part => part.Equals("steamapps", StringComparison.OrdinalIgnoreCase))) return false;
        var name = Path.GetFileName(normalized);
        return name.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) && ClientDirectories.Any(root =>
            normalized.Equals(Path.Combine(root, "steam.exe"), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class SteamLibraryCatalog
{
    private readonly Func<IEnumerable<string>> _readRoots;
    public SteamLibraryCatalog(Func<IEnumerable<string>>? readRoots = null) => _readRoots = readRoots ?? ReadRegisteredRoots;
    public SteamLibraryIndex Scan(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var warnings = new List<string>(); var clients = new List<string>(); var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var games = new List<SteamGame>(); var clock = Stopwatch.StartNew();
        bool Available() => clock.Elapsed < TimeSpan.FromSeconds(3) && games.Count < 4096;
        void Warn(string message) { if (warnings.Count < 40) warnings.Add(L.T(message)); }
        try
        {
            foreach (var value in _readRoots().Take(16))
            {
                token.ThrowIfCancellationRequested(); if (!Available()) break;
                var root = DisplayPath.Normalize(value);
                if (root.Length == 0 || !File.Exists(Path.Combine(root, "steam.exe"))) continue;
                try
                {
                    AutorunPathSafety.RejectReparseAncestors(root); clients.Add(root); libraries.Add(root);
                    foreach (var relative in new[] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
                    {
                        var path = Path.Combine(root, relative);
                        if (!File.Exists(path)) continue;
                        foreach (var library in ReadLibraryFolders(ReadText(path, 1024 * 1024, token)))
                        { if (libraries.Count >= 16) break; libraries.Add(library); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { Warn("部分 Steam 库配置无法读取，未猜测游戏归属。"); }
            }
            foreach (var library in libraries.Take(16))
            {
                token.ThrowIfCancellationRequested(); if (!Available()) break;
                var directory = Path.Combine(library, "steamapps");
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    AutorunPathSafety.RejectReparseAncestors(directory);
                    foreach (var manifest in Directory.EnumerateFiles(directory, "appmanifest_*.acf").Take(4096))
                    {
                        token.ThrowIfCancellationRequested(); if (!Available()) break;
                        try
                        {
                            var game = ReadAppManifest(ReadText(manifest, 128 * 1024, token), library, Path.GetFileName(manifest));
                            if (game is null || !Directory.Exists(game.InstallDirectory)) continue;
                            AutorunPathSafety.RejectReparseAncestors(game.InstallDirectory);
                            games.Add(game);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch { Warn("部分 Steam 游戏清单无法核实，保持原有分组。"); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { Warn("部分 Steam 库配置无法读取，未猜测游戏归属。"); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { Warn("部分 Steam 库配置无法读取，未猜测游戏归属。"); }
        if (!Available()) warnings.Add(L.T("Steam 库读取达到本轮上限，未识别的应用保持原有分组。"));
        return new(games, clients.Distinct(StringComparer.OrdinalIgnoreCase), warnings.Distinct());
    }

    public static IReadOnlyList<string> ReadLibraryFolders(string text)
    {
        var document = ValveText.Parse(text, 1024 * 1024);
        if (!document.TryGetValue("libraryfolders", out var container) || container.Children is null) throw new InvalidDataException();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in container.Children)
        {
            if (!uint.TryParse(pair.Key, out _)) continue;
            var raw = pair.Value.Value ?? (pair.Value.Children?.TryGetValue("path", out var path) == true ? path.Value : null);
            var normalized = DisplayPath.Normalize(raw);
            if (normalized.Length > 0 && result.Count < 16) result.Add(normalized);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    public static SteamGame? ReadAppManifest(string text, string libraryDirectory, string fileName)
    {
        var document = ValveText.Parse(text, 128 * 1024);
        if (!document.TryGetValue("AppState", out var app) || app.Children is null) return null;
        string Get(string key) => app.Children.TryGetValue(key, out var value) ? value.Value ?? "" : "";
        var idText = Get("appid");
        if (!uint.TryParse(idText, out var id) || id == 0 || idText != id.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            !fileName.Equals("appmanifest_" + idText + ".acf", StringComparison.OrdinalIgnoreCase)) return null;
        var name = Get("name"); var install = Get("installdir"); var library = DisplayPath.Normalize(libraryDirectory);
        if (name.Length == 0 || name.Length > 512 || name.Any(char.IsControl) || library.Length == 0 ||
            install.Length == 0 || install.Length > 255 || install is "." or ".." || install != install.Trim() ||
            install.EndsWith(".", StringComparison.Ordinal) || install.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        // Read a complete installation only. Downloading/staging metadata cannot claim a game folder.
        if (!uint.TryParse(Get("StateFlags"), out var state) || (state & 4) == 0) return null;
        var directory = DisplayPath.Normalize(Path.Combine(library, "steamapps", "common", install));
        return directory.Length == 0 ? null : new(id, name, directory, library);
    }

    internal static string ReadText(string path, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); AutorunPathSafety.RejectReparseAncestors(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > limit) throw new InvalidDataException();
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true);
        var buffer = new char[4096]; var result = new StringBuilder(); int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        { token.ThrowIfCancellationRequested(); if (result.Length + read > limit) throw new InvalidDataException(); result.Append(buffer, 0, read); }
        return result.ToString();
    }
    private static IEnumerable<string> ReadRegisteredRoots()
    {
        var result = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view); using var key = root.OpenSubKey(@"Software\Valve\Steam");
                var path = InstallationRegistryText.Read(key, hive == RegistryHive.CurrentUser ? "SteamPath" : "InstallPath");
                if (path.Length > 0) result.Add(path);
            }
            catch { /* Unreadable registrations provide no evidence. */ }
        }
        return result;
    }
}

internal static class InstallationRegistryText
{
    internal static string Read(RegistryKey? key, string name)
    {
        if (key is null) return "";
        uint size = 0;
        if (RegQueryValueEx(key.Handle, name, nint.Zero, out var type, null, ref size) != 0 || type is not (1 or 2) || size is < 2 or > 65536 || size % 2 != 0) return "";
        var bytes = new byte[size];
        if (RegQueryValueEx(key.Handle, name, nint.Zero, out type, bytes, ref size) != 0 || type is not (1 or 2) || size < 2 || size > bytes.Length || size % 2 != 0) return "";
        var text = new UnicodeEncoding(false, false, true).GetString(bytes, 0, (int)size).TrimEnd('\0');
        return text.IndexOf('\0') >= 0 ? "" : text; // REG_EXPAND_SZ remains unexpanded installation evidence.
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, nint reserved, out int type, byte[]? data, ref uint length);
}

internal static class DisplayPath
{
    internal static string Normalize(string? path)
    {
        if (path is null || string.IsNullOrWhiteSpace(path) || path.Length > 32767 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0) return "";
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); } catch { return ""; }
    }
    internal static bool Under(string path, string root) => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

internal static class ValveText
{
    internal sealed record Node(string? Value, Dictionary<string, Node>? Children);
    internal static Dictionary<string, Node> Parse(string text, int limit)
    {
        if (text is null || text.Length > limit || text.IndexOf('\0') >= 0) throw new InvalidDataException();
        int offset = 0, nodes = 0;
        string? Next()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || text[offset] == '\ufeff') { offset++; continue; }
                if (text[offset] == '/' && offset + 1 < text.Length && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
                if (offset == text!.Length) return null;
            char first = text[offset++]; if (first is '{' or '}') return first.ToString();
            var value = new StringBuilder();
            if (first != '"')
            {
                value.Append(first); while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '{' and not '}') value.Append(text[offset++]);
                return value.ToString();
            }
            while (offset < text.Length)
            {
                char current = text[offset++]; if (current == '"') return value.ToString();
                if (current == '\\' && offset < text.Length && text[offset] is '\\' or '"') current = text[offset++];
                value.Append(current); if (value.Length > 32767) throw new InvalidDataException();
            }
            throw new InvalidDataException();
        }
        Dictionary<string, Node> Object(int depth, bool nested)
        {
            if (depth > 12) throw new InvalidDataException(); var result = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var key = Next(); if (key is null) { if (nested) throw new InvalidDataException(); return result; }
                if (key == "}") { if (!nested) throw new InvalidDataException(); return result; }
                if (key == "{" || ++nodes > 12000 || result.ContainsKey(key)) throw new InvalidDataException();
                var value = Next(); if (value is null || value == "}") throw new InvalidDataException();
                result.Add(key, value == "{" ? new(null, Object(depth + 1, true)) : new(value, null));
            }
        }
        return Object(0, false);
    }
}
