using System.Diagnostics;

namespace ProcessKeeper.Core;

/// <summary>Immutable display evidence. Never used as protection, mutation or process-close authority.</summary>
public sealed partial class ApplicationDisplayCatalog
{
    public static ApplicationDisplayCatalog Empty { get; } = new([], [], []);
    private readonly GameCatalogEntry[] _games;
    private readonly GamePlatformClient[] _clients;
    private readonly Dictionary<string, GameCatalogEntry[]> _gameDirectories;
    private readonly HashSet<string> _microsoft;
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<GameCatalogEntry> Games { get; }
    public IReadOnlyList<GamePlatformClient> Clients { get; }
    public bool HasSamePresentationAs(ApplicationDisplayCatalog other) =>
        _games.SequenceEqual(other._games) && _clients.SequenceEqual(other._clients) && _microsoft.SetEquals(other._microsoft);
    /// <summary>The arguments are verified display evidence supplied by Capture; the constructor also supports isolated UI fixtures.</summary>
    public ApplicationDisplayCatalog(IEnumerable<GameCatalogEntry> games, IEnumerable<GamePlatformClient> clients, IEnumerable<string> verifiedMicrosoftPaths, IEnumerable<string>? warnings = null)
    {
        _games = games.Take(8192).Where(game => game is not null && DisplayPath.Normalize(game.InstallDirectory).Length > 3)
            .Select(game => game with { InstallDirectory = DisplayPath.Normalize(game.InstallDirectory),
                IconPath = DisplayPath.Normalize(game.IconPath), ExecutablePath = DisplayPath.Normalize(game.ExecutablePath) }).Distinct().ToArray();
        _clients = clients.Take(32).Where(client => client is not null && DisplayPath.Normalize(client.ExecutablePath).Length > 0)
            .Select(client => client with { ExecutablePath = DisplayPath.Normalize(client.ExecutablePath) }).Distinct().ToArray();
        _microsoft = new(verifiedMicrosoftPaths.Take(20000).Select(DisplayPath.Normalize).Where(path => path.Length > 0), StringComparer.OrdinalIgnoreCase);
        Warnings = Array.AsReadOnly((warnings ?? []).Distinct().Take(40).ToArray());
        Games = Array.AsReadOnly(_games); Clients = Array.AsReadOnly(_clients);
        _gameDirectories = _games.GroupBy(game => game.InstallDirectory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
    }
    public GameCatalogEntry? FindGame(ProcessRecord process) => FindGamePath(process.Path);
    public GameCatalogEntry? FindGame(ApplicationGroup app) => CommonGame(app.Processes.Select(process => process.Path));
    public GameCatalogEntry? FindGame(InstalledApplication app) => CommonGame(app.Executables.Select(executable => executable.Path));
    public GameCatalogEntry? FindGame(AutorunEntry entry) => SimpleAutorun(entry) ? FindGamePath(entry.TargetPath) : null;
    public GamePlatform? FindGamePlatform(ApplicationGroup app) => FindClientPlatform(app) ?? FindGame(app)?.Platform;
    public GamePlatform? FindGamePlatform(InstalledApplication app) => FindClientPlatform(app) ?? FindGame(app)?.Platform;
    public GamePlatform? FindGamePlatform(AutorunEntry entry) => SimpleAutorun(entry) ? FindClient(entry.TargetPath) ?? FindGame(entry)?.Platform : null;
    public GamePlatform? FindClientPlatform(ApplicationGroup app) => CommonClient(app.Processes.Select(process => process.Path));
    public GamePlatform? FindClientPlatform(InstalledApplication app) => CommonClient(app.Executables.Select(executable => executable.Path));
    public bool IsMicrosoft(ProcessRecord process) => IsMicrosoftPath(process.Path);
    public bool IsMicrosoft(ApplicationGroup app) => AllMicrosoft(app.Processes.Select(process => process.Path));
    public bool IsMicrosoft(InstalledApplication app) => AllMicrosoft(app.Executables.Select(executable => executable.Path));
    public bool IsMicrosoft(AutorunEntry entry) => entry.Ownership == AutorunOwnership.Windows ||
        (SimpleAutorun(entry) || entry.SourceKind == AutorunSourceKind.PackagedStartup) && IsMicrosoftPath(entry.TargetPath);
    private bool AllMicrosoft(IEnumerable<string> source)
    { var paths = source.Take(20001).ToArray(); return paths.Length is > 0 and <= 20000 && paths.All(IsMicrosoftPath); }
    private bool IsMicrosoftPath(string path)
    { var normalized = DisplayPath.Normalize(path); return normalized.Length > 0 && !SharedHosts.Contains(Path.GetFileName(normalized)) && _microsoft.Contains(normalized); }
    private GameCatalogEntry? FindGamePath(string path) => FindGamePath(path, out _);
    private GameCatalogEntry? FindGamePath(string path, out bool ambiguous)
    {
        ambiguous = false;
        var normalized = DisplayPath.Normalize(path); if (normalized.Length == 0) return null;
        GameCatalogEntry? match = null; var directory = Path.GetDirectoryName(normalized); int depth = 0;
        while (!string.IsNullOrEmpty(directory) && ++depth <= 128)
        {
            if (_gameDirectories.TryGetValue(directory, out var matches))
            { if (matches.Length != 1 || match is not null) { ambiguous = true; return null; } match = matches[0]; }
            directory = Path.GetDirectoryName(directory);
        }
        ambiguous = depth > 128;
        return ambiguous ? null : match;
    }
    private GameCatalogEntry? CommonGame(IEnumerable<string> source)
    {
        GameCatalogEntry? result = null; int count = 0;
        foreach (var path in source)
        { if (++count > 20000) return null; var game = FindGamePath(path); if (game is null || result is not null && result != game) return null; result = game; }
        return result;
    }
    private GamePlatform? FindClient(string path)
    {
        var normalized = DisplayPath.Normalize(path); if (normalized.Length == 0 || FindGamePath(path) is not null) return null;
        var matches = _clients.Where(client => client.ExecutablePath.Equals(normalized, StringComparison.OrdinalIgnoreCase)).Select(client => client.Platform).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private GamePlatform? CommonClient(IEnumerable<string> source)
    {
        var paths = source.Take(20001).ToArray(); if (paths.Length is 0 or > 20000 || paths.Any(path => FindGamePath(path) is not null)) return null;
        var exact = paths.Select(FindClient).Where(platform => platform is not null).Distinct().Take(2).ToArray();
        if (exact.Length != 1) return null;
        var platform = exact[0]!;
        if (platform.Id == "steam" && paths.Any(path => path.Split('\\', '/').Any(part => part.Equals("steamapps", StringComparison.OrdinalIgnoreCase)))) return null;
        var directories = _clients.Where(client => client.Platform == platform).Select(client => Path.GetDirectoryName(client.ExecutablePath)!).ToArray();
        return paths.All(path => { var normalized = DisplayPath.Normalize(path); return normalized.Length > 0 && directories.Any(root => DisplayPath.Under(normalized, root)); }) ? platform : null;
    }
    private static bool SimpleAutorun(AutorunEntry entry) => entry.SourceKind is AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or AutorunSourceKind.PolicyRun or AutorunSourceKind.Service or AutorunSourceKind.Driver &&
        !SharedHosts.Contains(Path.GetFileName(entry.TargetPath));
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "svchost.exe", "rundll32.exe", "regsvr32.exe", "msiexec.exe", "wscript.exe", "cscript.exe", "mshta.exe", "dllhost.exe", "conhost.exe", "taskhost.exe", "taskhostw.exe", "sihost.exe", "explorer.exe" };
}

/// <summary>Call on a background worker. Bounded metadata reads and a single native publisher worker protect UI responsiveness.</summary>
public sealed class ApplicationDisplayService
{
    private sealed record CachedPublisher(PublisherFileIdentity Identity, bool Microsoft, DateTimeOffset CheckedAt);
    private sealed record PendingPublisher(string Path, PublisherFileIdentity Identity, Task<bool> Work);
    private readonly object _captureLock = new();
    private readonly Func<CancellationToken, GamePlatformSnapshot> _scanGames;
    private readonly IMicrosoftPublisherProbe _probe;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, CachedPublisher> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private PendingPublisher? _pending;
    private GamePlatformSnapshot _platforms = new([], [], []);
    private DateTimeOffset _platformsAt = DateTimeOffset.MinValue;
    public ApplicationDisplayService(Func<CancellationToken, GamePlatformSnapshot>? scanGames = null, IMicrosoftPublisherProbe? publisherProbe = null, Func<DateTimeOffset>? utcNow = null)
    { _scanGames = scanGames ?? new GamePlatformCatalog().Scan; _probe = publisherProbe ?? new MicrosoftPublisherProbe(); _now = utcNow ?? (() => DateTimeOffset.UtcNow); }
    public ApplicationDisplayCatalog Capture(ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication> installed, IReadOnlyList<AutorunEntry> autoruns, CancellationToken token = default)
        => Capture(snapshot, installed, autoruns, true, token);

    public ApplicationDisplayCatalog Capture(ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication> installed,
        IReadOnlyList<AutorunEntry> autoruns, bool verifyMicrosoft, CancellationToken token = default)
        => Capture(snapshot, installed, autoruns, Array.Empty<UninstallEntry>(), verifyMicrosoft, token);

    public ApplicationDisplayCatalog Capture(ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication> installed,
        IReadOnlyList<AutorunEntry> autoruns, IReadOnlyList<UninstallEntry> uninstall, bool verifyMicrosoft, CancellationToken token = default)
    {
        lock (_captureLock)
        {
            token.ThrowIfCancellationRequested(); var now = _now();
            if (now - _platformsAt >= TimeSpan.FromMinutes(1))
            { _platforms = _scanGames(token); _platformsAt = now; }
            var warnings = _platforms.Warnings.ToList();
            // Grouping games needs only launcher catalogs, not thousands of file identity/signature reads.
            if (!verifyMicrosoft) return new(_platforms.Games, _platforms.Clients, [], warnings);
            var paths = snapshot.Processes.Select(process => process.Path).Concat(installed.SelectMany(app => app.Executables).Select(executable => executable.Path))
                .Concat(autoruns.Select(entry => entry.TargetPath)).Concat(uninstall.Take(20000).SelectMany(ApplicationDisplayCatalog.UninstallEvidencePaths))
                .Select(DisplayPath.Normalize).Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20000).ToArray();
            var verified = new List<string>(); var clock = Stopwatch.StartNew(); int started = 0;
            CompletePending();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested(); if (clock.Elapsed >= TimeSpan.FromSeconds(2)) break;
                var identity = _probe.ReadIdentity(path); if (identity is null) { _cache.Remove(path); continue; }
                if (_cache.TryGetValue(path, out var known) && known.Identity == identity && now - known.CheckedAt < TimeSpan.FromMinutes(5))
                { if (known.Microsoft) verified.Add(path); continue; }
                _cache.Remove(path);
                CompletePending();
                if (_pending is not null || started >= 128 || !NativeGate.Wait(0)) continue;
                started++;
                var work = Task.Run(() =>
                {
                    try { return _probe.IsMicrosoft(path, CancellationToken.None) && _probe.ReadIdentity(path) == identity; }
                    catch { return false; }
                    finally { NativeGate.Release(); }
                });
                _pending = new(path, identity, work);
                // The timeout never releases the worker slot early. A stuck native provider cannot spawn more workers.
                if (work.Wait(200, token))
                { CompletePending(); if (_cache.TryGetValue(path, out known) && known.Identity == identity && known.Microsoft) verified.Add(path); }
            }
            if (clock.Elapsed >= TimeSpan.FromSeconds(2) || _pending is not null || started >= 128)
                warnings.Add(L.T("部分发布者仍未核实，相关应用暂时保留显示。"));
            return new(_platforms.Games, _platforms.Clients, verified, warnings);

            void CompletePending()
            {
                if (_pending is null || !_pending.Work.IsCompleted) return;
                if (_cache.Count >= 4096) _cache.Remove(_cache.OrderBy(pair => pair.Value.CheckedAt).First().Key);
                _cache[_pending.Path] = new(_pending.Identity, _pending.Work.Status == TaskStatus.RanToCompletion && _pending.Work.Result, _now());
                _pending = null;
            }
        }
    }
}
