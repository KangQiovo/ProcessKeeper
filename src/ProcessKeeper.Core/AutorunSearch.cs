namespace ProcessKeeper.Core;

/// <summary>File/service identity links, not claims that a startup entry launched a process.</summary>
public sealed class AutorunSearchIndex
{
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "rundll32.exe", "mshta.exe", "regsvr32.exe", "svchost.exe", "dllhost.exe", "taskhostw.exe", "conhost.exe", "backgroundTaskHost.exe" };
    private readonly Dictionary<string, ProcessRecord[]> _byPath;
    private readonly Dictionary<string, ProcessRecord[]> _byService;
    private readonly Dictionary<string, string[]> _applications;

    public AutorunSearchIndex(ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication>? installed = null)
    {
        _byPath = snapshot.Processes.Where(p => Normalize(p.Path).Length > 0 && !SharedHosts.Contains(Path.GetFileName(p.Path)))
            .GroupBy(p => Normalize(p.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        _byService = snapshot.Processes.SelectMany(p => p.Services.Select(s => (s.Name, Process: p)))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Process).DistinctBy(p => p.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        _applications = (installed ?? []).SelectMany(app => app.Executables.Select(exe => (Path: Normalize(exe.Path), app.Name)))
            .Where(p => p.Path.Length > 0).GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ProcessRecord> Processes(AutorunEntry entry) => entry.SourceKind is AutorunSourceKind.Service or AutorunSourceKind.Driver
        ? _byService.GetValueOrDefault(entry.ServiceName) ?? []
        : _byPath.GetValueOrDefault(Normalize(entry.TargetPath)) ?? [];

    public IReadOnlyList<string> Applications(AutorunEntry entry) => _applications.GetValueOrDefault(Normalize(entry.TargetPath)) ?? [];

    public bool Matches(AutorunEntry entry, string query) => query.Length == 0 ||
        new[] { entry.Name, entry.Location, entry.Command, entry.TargetPath, entry.Scope, entry.TriggerSummary, entry.ServiceName }
            .Any(value => ApplicationSearch.Contains(value, query)) ||
        Processes(entry).Any(process => ApplicationSearch.MatchesProcess(process, query)) ||
        Applications(entry).Any(name => ApplicationSearch.Contains(name, query));

    private static string Normalize(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) : ""; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }
}
