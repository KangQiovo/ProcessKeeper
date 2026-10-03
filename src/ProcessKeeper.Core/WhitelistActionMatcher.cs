namespace ProcessKeeper.Core;

public static class WhitelistActionMatcher
{
    /// <summary>Matches known application identities and concrete targets, never a display-name substring.</summary>
    public static bool Matches(IEnumerable<string> paths, IReadOnlyList<WhitelistRule> rules,
        ProcessSnapshot snapshot, IEnumerable<InstalledApplication> installed)
    {
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var known = snapshot.Processes.Where(p => SamePath(p.Path, path)).ToArray();
            if (known.Any(p => ProtectionPolicy.IsDirectlyWhitelisted(p, rules))) return true;
            var identity = installed.FirstOrDefault(app => app.Executables.Any(exe => SamePath(exe.Path, path)));
            var target = ProcessIdentity.Classify(new ProcessRecord { Path = path, Name = Path.GetFileName(path) });
            if (identity is not null) target = target with { ApplicationKey = identity.ApplicationKey };
            if (ProtectionPolicy.IsDirectlyWhitelisted(target, rules)) return true;
        }
        return false;
    }
    private static bool SamePath(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return false; }
    }
}
