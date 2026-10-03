using System.Globalization;

namespace ProcessKeeper.Core;

/// <summary>Shared read-only matching. Matches never change process ownership or protection.</summary>
public static class ApplicationSearch
{
    public static bool Contains(string? value, string query) =>
        query.Length == 0 || (value?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    public static bool MatchesProcess(ProcessRecord process, string query) =>
        Contains(process.Name, query) || Contains(process.Path, query) ||
        Contains(process.Description, query) || Contains(process.ProductName, query) ||
        Contains(process.ApplicationName, query) || Contains(process.Company, query) ||
        process.Id.ToString(CultureInfo.InvariantCulture).Equals(query, StringComparison.Ordinal);

    public static bool MatchesApplicationMetadata(ApplicationGroup app, string query) =>
        Contains(app.Name, query) || Contains(app.Company, query) || Contains(app.Description, query);

    public static bool MatchesApplication(ApplicationGroup app, string query) =>
        MatchesApplicationMetadata(app, query) || app.Processes.Any(process => MatchesProcess(process, query));

    public static bool MatchesRule(WhitelistRule rule, string displayName,
        IEnumerable<RuleProcessMatch> visibleProcesses, string query) =>
        Contains(displayName, query) || Contains(rule.Name, query) || Contains(rule.Value, query) ||
        visibleProcesses.Any(match => MatchesProcess(match.Process, query));
}

/// <summary>Installation metadata only; no filesystem reads or invented running processes.</summary>
public sealed class RuleSearchIndex
{
    private readonly IReadOnlyList<InstalledApplication> _applications;
    public RuleSearchIndex(IReadOnlyList<InstalledApplication> applications) => _applications = applications;

    public HashSet<string> MatchingRuleIds(IReadOnlyList<WhitelistRule> rules, string query)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _applications)
        {
            var applicationMatches = InstalledApplicationSearch.MatchesApplication(app, query);
            if (applicationMatches && app.ApplicationKey.Length > 0)
                keys.Add(app.ApplicationKey);
            foreach (var executable in app.Executables)
            {
                if (!applicationMatches && !ApplicationSearch.Contains(executable.Name, query) &&
                    !ApplicationSearch.Contains(executable.Description, query) && !ApplicationSearch.Contains(executable.Path, query)) continue;
                // A component hit also finds the owning installation's application rule.
                // This is search metadata only, not a claim that the rule protects that file.
                if (app.ApplicationKey.Length > 0) keys.Add(app.ApplicationKey);
                if (executable.ApplicationKey.Length > 0) keys.Add(executable.ApplicationKey);
                names.Add(Path.GetFileName(executable.Path));
                if (ProtectionPolicy.TryNormalizePath(executable.Path, out var path)) paths.Add(path);
            }
        }
        var sortedPaths = paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return rules.Where(rule => rule.Kind switch
            {
                RuleKind.Application => keys.Contains(rule.Value),
                RuleKind.ProcessName => names.Contains(rule.Value),
                RuleKind.ExecutablePath => ProtectionPolicy.TryNormalizePath(rule.Value, out var path) && paths.Contains(path),
                RuleKind.Directory => ProtectionPolicy.TryNormalizePath(rule.Value, out var directory) && ContainsDirectory(directory),
                _ => false
            })
            .Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);

        bool ContainsDirectory(string directory)
        {
            var prefix = directory.TrimEnd('\\') + "\\";
            var position = Array.BinarySearch(sortedPaths, prefix, StringComparer.OrdinalIgnoreCase);
            if (position < 0) position = ~position;
            return position < sortedPaths.Length && sortedPaths[position].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
