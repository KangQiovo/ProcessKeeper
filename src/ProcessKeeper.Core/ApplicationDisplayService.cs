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
    private readonly HashSet<string> _microsoftPackages;
    public IReadOnlyList<string> Warnings { get; }
    public IReadOnlyList<GameCatalogEntry> Games { get; }
    public IReadOnlyList<GamePlatformClient> Clients { get; }
    /// <summary>Only unfinished publisher work requests a later capture. Completed negative/unreadable evidence does not poll.</summary>
    public bool RequiresMicrosoftRefresh { get; }
    public bool HasSamePresentationAs(ApplicationDisplayCatalog other) =>
        _games.SequenceEqual(other._games) && _clients.SequenceEqual(other._clients) && _microsoft.SetEquals(other._microsoft) &&
        _microsoftPackages.SetEquals(other._microsoftPackages);
    /// <summary>The arguments are verified display evidence supplied by Capture; the constructor also supports isolated UI fixtures.</summary>
    public ApplicationDisplayCatalog(IEnumerable<GameCatalogEntry> games, IEnumerable<GamePlatformClient> clients, IEnumerable<string> verifiedMicrosoftPaths,
        IEnumerable<string>? warnings = null, IEnumerable<string>? verifiedMicrosoftPackages = null, bool requiresMicrosoftRefresh = false)
    {
        _games = games.Take(8192).Where(game => game is not null && DisplayPath.Normalize(game.InstallDirectory).Length > 3)
            .Select(game => game with { InstallDirectory = DisplayPath.Normalize(game.InstallDirectory),
                IconPath = DisplayPath.Normalize(game.IconPath), ExecutablePath = DisplayPath.Normalize(game.ExecutablePath) }).Distinct().ToArray();
        _clients = clients.Take(32).Where(client => client is not null && DisplayPath.Normalize(client.ExecutablePath).Length > 0)
            .Select(client => client with { ExecutablePath = DisplayPath.Normalize(client.ExecutablePath) }).Distinct().ToArray();
        _microsoft = new(verifiedMicrosoftPaths.Take(20000).Select(DisplayPath.Normalize).Where(path => path.Length > 0), StringComparer.OrdinalIgnoreCase);
        _microsoftPackages = new((verifiedMicrosoftPackages ?? []).Take(2500), StringComparer.OrdinalIgnoreCase);
        RequiresMicrosoftRefresh = requiresMicrosoftRefresh;
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
    public bool IsMicrosoft(ApplicationGroup app) =>
        (app.Company.Length == 0 || MicrosoftDesktopPublisher(app.Company)) &&
        !app.Processes.Any(process => ApplicationPresentationGroups.IsSharedHost(process.Path)) &&
        AllMicrosoft(app.Processes.Select(process => process.Path));
    public bool IsMicrosoft(InstalledApplication app)
    {
        var originals = OriginalInstallations(new[] { app });
        if (originals is null || originals.Length == 0 || !originals.All(IsMicrosoftInstallation)) return false;
        if (app.Installations.Count == 0) return true;
        // The retained original records must account for every aggregate component. A presentation
        // family's first member cannot confer its package trust on an unrelated installation.
        var originalPaths = new HashSet<string>(originals.SelectMany(original => original.Executables)
            .Select(executable => DisplayPath.Normalize(executable.Path)), StringComparer.OrdinalIgnoreCase);
        return app.Executables.Count <= 20000 && app.Executables.All(executable => originalPaths.Contains(DisplayPath.Normalize(executable.Path)));
    }
    private bool IsMicrosoftInstallation(InstalledApplication app)
    {
        if (!TryMicrosoftPackage(app, out var declared) &&
            !(TryMicrosoftPackageCandidate(app, out declared) && _microsoftPackages.Contains(PackagePresentationKey(app))))
        {
            // A Microsoft-signed shared runtime/helper does not make an explicitly third-party
            // registered product Microsoft. Publisher labels only veto hiding; they cannot grant it.
            if (app.Publisher.Length > 0 && !MicrosoftDesktopPublisher(app.Publisher)) return false;
            return AllMicrosoft(app.Executables.Select(executable => executable.Path));
        }
        return app.Executables.Count <= 20000 && app.Executables.All(executable =>
            !SharedHosts.Contains(Path.GetFileName(executable.Path)) &&
            (declared.Contains(DisplayPath.Normalize(executable.Path)) || IsMicrosoftPath(executable.Path)));
    }
    private static bool MicrosoftDesktopPublisher(string publisher) =>
        MicrosoftPublisherNames.Any(value => publisher.Trim().Equals(value, StringComparison.OrdinalIgnoreCase));
    private static readonly string[] MicrosoftPublisherNames =
        { "Microsoft Corporation", "Microsoft", "Microsoft Corp.", "微软公司", "微軟公司", "微软", "微軟" };
    internal static bool TryMicrosoftPackage(InstalledApplication app, out HashSet<string> declared)
    {
        declared = new(StringComparer.OrdinalIgnoreCase);
        var evidence = app.MicrosoftPackageEvidence;
        if (evidence is null || evidence.ExecutablePaths.Count > 20000 ||
            !MicrosoftPublisherProbe.IsMicrosoftPackageFullName(evidence.FullName)) return false;
        var identity = evidence.FullName.Split('_');
        var root = DisplayPath.Normalize(evidence.InstallLocation);
        if (root.Length <= 3 || !evidence.FamilyName.Equals(identity[0] + "_" + identity[4], StringComparison.Ordinal) ||
            !app.ApplicationKey.Equals("package:" + evidence.FamilyName, StringComparison.OrdinalIgnoreCase) ||
            !DisplayPath.Normalize(app.InstallLocation).Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var path in evidence.ExecutablePaths)
        {
            var normalized = DisplayPath.Normalize(path);
            if (normalized.Length == 0 || !DisplayPath.Under(normalized, root) || SharedHosts.Contains(Path.GetFileName(normalized))) return false;
            declared.Add(normalized);
        }
        return true;
    }
    internal static bool TryMicrosoftPackageCandidate(InstalledApplication app, out HashSet<string> declared)
    {
        declared = new(StringComparer.OrdinalIgnoreCase);
        var candidate = app.MicrosoftPackageSignatureCandidate;
        if (candidate is null || !MicrosoftPublisherProbe.IsMicrosoftPackageFullName(candidate.FullName)) return false;
        var identity = candidate.FullName.Split('_');
        if (!MicrosoftPublisherProbe.HasMicrosoftPackagePublisherIdentity(candidate.Publisher, identity[4]) ||
            !DisplayPath.Normalize(candidate.SignaturePath).Equals(
                DisplayPath.Normalize(Path.Combine(candidate.InstallLocation, "AppxSignature.p7x")), StringComparison.OrdinalIgnoreCase)) return false;
        return TryMicrosoftPackage(app with { MicrosoftPackageEvidence = new(candidate.FamilyName, candidate.FullName,
            candidate.InstallLocation, candidate.ExecutablePaths) }, out declared);
    }
    internal static InstalledApplication[]? OriginalInstallations(IEnumerable<InstalledApplication> source)
    {
        var pending = new Stack<InstalledApplication>(source.Take(2501));
        var originals = new List<InstalledApplication>(); int visited = 0;
        while (pending.Count > 0)
        {
            if (++visited > 5000 || pending.Count > 2500 || originals.Count >= 2500) return null;
            var app = pending.Pop();
            if (app.Installations.Count == 0) originals.Add(app);
            else
            {
                if (app.Installations.Count > 2500) return null;
                foreach (var member in app.Installations) pending.Push(member);
            }
        }
        return originals.ToArray();
    }
    internal static string PackagePresentationKey(InstalledApplication app) => app.Id + "|" + app.ApplicationKey + "|" +
        DisplayPath.Normalize(app.InstallLocation) + "|" + (app.MicrosoftPackageEvidence?.FullName ?? app.MicrosoftPackageSignatureCandidate!.FullName) + "|" +
        string.Join("|", (app.MicrosoftPackageEvidence?.ExecutablePaths ?? app.MicrosoftPackageSignatureCandidate!.ExecutablePaths)
            .Select(DisplayPath.Normalize).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    public bool IsMicrosoft(AutorunEntry entry) => entry.Ownership == AutorunOwnership.Windows ||
        entry.Ownership != AutorunOwnership.ThirdParty &&
        (SimpleAutorun(entry) || entry.SourceKind == AutorunSourceKind.PackagedStartup) &&
        !ApplicationPresentationGroups.IsSharedHost(entry.TargetPath) && IsMicrosoftPath(entry.TargetPath);
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
    private sealed record PendingPublisher(string Path, PublisherFileIdentity Identity, Task<bool?> Work);
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
            var originals = ApplicationDisplayCatalog.OriginalInstallations(installed) ?? [];
            var packagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packageKeys = new List<string>();
            var packageSignatures = new Dictionary<string, List<(InstalledApplication App, HashSet<string> Paths)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in originals)
            {
                token.ThrowIfCancellationRequested();
                if (ApplicationDisplayCatalog.TryMicrosoftPackage(app, out var declared))
                {
                    packageKeys.Add(ApplicationDisplayCatalog.PackagePresentationKey(app));
                    foreach (var path in declared.Take(Math.Max(0, 20000 - packagePaths.Count))) packagePaths.Add(path);
                }
                else if (ApplicationDisplayCatalog.TryMicrosoftPackageCandidate(app, out declared))
                {
                    var signature = DisplayPath.Normalize(app.MicrosoftPackageSignatureCandidate!.SignaturePath);
                    if (!packageSignatures.TryGetValue(signature, out var owners)) packageSignatures[signature] = owners = new();
                    owners.Add((app, declared));
                }
            }
            var paths = packagePaths.Concat(packageSignatures.Keys).Concat(snapshot.Processes.Select(process => process.Path)).Concat(originals.SelectMany(app => app.Executables).Select(executable => executable.Path))
                .Concat(autoruns.Select(entry => entry.TargetPath)).Concat(uninstall.Take(20000).SelectMany(ApplicationDisplayCatalog.UninstallEvidencePaths))
                .Select(DisplayPath.Normalize).Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(20000).ToArray();
            var verified = new List<string>(); var clock = Stopwatch.StartNew(); int started = 0; bool incomplete = false;
            CompletePending();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested(); if (clock.Elapsed >= TimeSpan.FromSeconds(2)) { incomplete = true; break; }
                var identity = _probe.ReadIdentity(path); if (identity is null) { _cache.Remove(path); continue; }
                // Package ownership is already verified in the inventory read. Recheck file safety/identity,
                // then reuse exact declared entries without native signer work or per-file package lookups.
                if (packagePaths.Contains(path)) { RecordVerified(path); continue; }
                if (_cache.TryGetValue(path, out var known) && known.Identity == identity && now - known.CheckedAt < TimeSpan.FromMinutes(5))
                { if (known.Microsoft) RecordVerified(path); continue; }
                _cache.Remove(path);
                CompletePending();
                if (_pending is not null || started >= 128 || !NativeGate.Wait(0)) { incomplete = true; continue; }
                started++;
                var work = Task.Run<bool?>(() =>
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        var result = _probe.IsMicrosoft(path, token);
                        token.ThrowIfCancellationRequested();
                        return result && _probe.ReadIdentity(path) == identity;
                    }
                    catch (OperationCanceledException) { return null; }
                    catch { return false; }
                    finally { NativeGate.Release(); }
                });
                _pending = new(path, identity, work);
                // The timeout never releases the worker slot early. A stuck native provider cannot spawn more workers.
                if (work.Wait(200, token))
                { CompletePending(); if (_cache.TryGetValue(path, out known) && known.Identity == identity && known.Microsoft) RecordVerified(path); }
            }
            // .NET Framework may complete Task.Wait before observing a concurrently cancelled token.
            token.ThrowIfCancellationRequested();
            incomplete |= _pending is not null;
            if (incomplete)
                warnings.Add(L.T("部分发布者仍未核实，相关应用暂时保留显示。"));
            return new(_platforms.Games, _platforms.Clients, verified, warnings, packageKeys, incomplete);

            void RecordVerified(string path)
            {
                verified.Add(path);
                if (!packageSignatures.TryGetValue(path, out var owners)) return;
                foreach (var owner in owners)
                {
                    packageKeys.Add(ApplicationDisplayCatalog.PackagePresentationKey(owner.App));
                    foreach (var declared in owner.Paths.Take(Math.Max(0, 20000 - packagePaths.Count))) packagePaths.Add(declared);
                }
            }

            void CompletePending()
            {
                if (_pending is null || !_pending.Work.IsCompleted) return;
                // Cancellation is unfinished evidence, not a five-minute negative publisher cache entry.
                if (_pending.Work.Status == TaskStatus.RanToCompletion && _pending.Work.Result is { } microsoft)
                {
                    if (_cache.Count >= 20000) _cache.Remove(_cache.OrderBy(pair => pair.Value.CheckedAt).First().Key);
                    _cache[_pending.Path] = new(_pending.Identity, microsoft, _now());
                }
                _pending = null;
            }
        }
    }
}
