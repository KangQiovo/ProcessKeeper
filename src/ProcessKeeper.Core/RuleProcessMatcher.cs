namespace ProcessKeeper.Core;

/// <summary>
/// Describes a rule's relationship to real snapshot processes, independently of whether the
/// rule is enabled or another safety rule already protects a process. It never grants protection.
/// </summary>
public sealed record RuleProcessMatch(ProcessRecord Process, bool IsInherited, int? MatchedAncestorId = null);

public sealed class RuleProcessMatcher
{
    private readonly IReadOnlyList<ProcessRecord> _processes;
    private readonly Dictionary<int, ProcessRecord> _parents;

    public RuleProcessMatcher(ProcessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _processes = snapshot.Processes;
        // Ambiguous PIDs must not be accepted as verified parents.
        _parents = snapshot.Processes.GroupBy(p => p.Id).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyList<RuleProcessMatch> Match(WhitelistRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var results = new List<RuleProcessMatch>();
        foreach (var process in _processes)
        {
            if (ProtectionPolicy.Matches(rule, process))
            {
                results.Add(new RuleProcessMatch(process, false));
                continue;
            }
            if (!rule.IncludeDescendants ||
                (ProtectionPolicy.IsDefaultCodex(rule) && ProtectionPolicy.IsExplicitCloseFamily(process))) continue;
            var seen = new HashSet<int> { process.Id };
            var child = process;
            for (var depth = 0; depth < 128; depth++)
            {
                if (child.ParentId <= 4 || !seen.Add(child.ParentId) ||
                    !_parents.TryGetValue(child.ParentId, out var parent) ||
                    !ProtectionPolicy.IsVerifiedParent(parent, child)) break;
                if (ProtectionPolicy.IsDefaultCodex(rule) && ProtectionPolicy.IsExplicitCloseFamily(parent)) break;
                if (ProtectionPolicy.Matches(rule, parent))
                {
                    results.Add(new RuleProcessMatch(process, true, parent.Id));
                    break;
                }
                child = parent;
            }
        }
        return results;
    }
}
