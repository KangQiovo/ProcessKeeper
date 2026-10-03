namespace ProcessKeeper.Core;

/// <summary>One snapshot index per query. Only exact executable paths establish installation ownership.</summary>
public sealed class InstalledApplicationSearch
{
    private readonly Dictionary<string, ProcessRecord[]> _byPath;

    public InstalledApplicationSearch(IReadOnlyList<ProcessRecord> processes, bool includeSystem)
    {
        _byPath = processes.Where(p => p.Path.Length > 0 && (includeSystem || p.Category != RunCategory.System))
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProcessRecord> ProcessesFor(InstalledExecutable executable) =>
        _byPath.GetValueOrDefault(executable.Path) ?? [];

    public static string DriveOf(string path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
        (path[2] == '\\' || path[2] == '/') ? char.ToUpperInvariant(path[0]) + ":" : "";

    public static IReadOnlyList<string> Drives(IEnumerable<InstalledApplication> applications) => applications
        .SelectMany(a => a.Executables.Select(e => DriveOf(e.Path)).Append(DriveOf(a.InstallLocation))
            .Concat(a.Installations.Select(member => DriveOf(member.InstallLocation))))
        .Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool OnDrive(InstalledApplication app, string drive) => drive.Length == 0 ||
        DriveOf(app.InstallLocation).Equals(drive, StringComparison.OrdinalIgnoreCase) ||
        app.Installations.Any(member => DriveOf(member.InstallLocation).Equals(drive, StringComparison.OrdinalIgnoreCase)) ||
        app.Executables.Any(e => DriveOf(e.Path).Equals(drive, StringComparison.OrdinalIgnoreCase));

    public static bool MatchesApplication(InstalledApplication app, string query) => Contains(app.Name, query) ||
        Contains(app.Publisher, query) || Contains(app.InstallLocation, query) ||
        app.Installations.Any(member => Contains(member.Name, query) || Contains(member.Publisher, query) || Contains(member.InstallLocation, query));

    public static bool MatchesExecutable(InstalledExecutable executable, string query) =>
        Contains(executable.Name, query) || Contains(executable.Description, query) || Contains(executable.Path, query);

    public bool MatchesComponent(InstalledExecutable executable, string query) => MatchesExecutable(executable, query) ||
        ProcessesFor(executable).Any(p => MatchesProcess(p, query));

    public bool Matches(InstalledApplication app, string query, string drive = "") => OnDrive(app, drive) &&
        (query.Length == 0 || MatchesApplication(app, query) || app.Executables.Any(e => MatchesComponent(e, query)));

    public static bool MatchesProcess(ProcessRecord process, string query) => ApplicationSearch.MatchesProcess(process, query);

    private static bool Contains(string value, string query) => value.Contains(query, StringComparison.OrdinalIgnoreCase);
}
