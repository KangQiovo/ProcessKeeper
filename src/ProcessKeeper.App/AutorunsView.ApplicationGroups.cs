using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView
{
    // App parents organize display and selection only. Their children retain the
    // native locator and fingerprint consumed by the existing mutation boundary.
    private static AutorunRow[] ApplicationRows(IReadOnlyList<AutorunApplicationGroup> groups,
        AutorunSearchIndex index, string query, int source, int state, bool complex,
        ApplicationDisplayCatalog display, bool hideMicrosoft, HashSet<string> expanded,
        HashSet<string> collapsedSearch)
    {
        var result = new List<AutorunRow>();
        foreach (var group in groups.OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            var appMatch = ApplicationSearch.Contains(group.Name, query);
            var entries = group.Entries.Where(entry => (!hideMicrosoft || !display.IsMicrosoft(entry)) &&
                AutorunPresentation.Matches(entry, complex, source) && StateMatches(entry, state) &&
                (query.Length == 0 || appMatch || index.Matches(entry, query) || MatchesPlatformQuery(entry, query, display)))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.SourceKind).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) continue;
            // A genuine single native entry keeps its existing one-row presentation.
            // Group existence is based on full inventory, so filtering does not change keys.
            if (group.Entries.Count == 1)
            {
                result.Add(CreateRow(entries[0], index, expanded.Contains(entries[0].Id), query,
                    collapsedSearch.Contains(entries[0].Id)));
                continue;
            }
            var open = query.Length > 0 ? !collapsedSearch.Contains(group.Key) : expanded.Contains(group.Key);
            var processes = entries.SelectMany(index.Processes).DistinctBy(process => (process.Id, process.StartTimeUtcTicks)).ToArray();
            result.Add(new AutorunRow
            {
                Entry = new AutorunEntry { Id = group.Key, Name = group.Name, TargetPath = group.IconPath },
                ApplicationEntries = group.Entries, ApplicationIconPath = group.IconPath,
                IsApplicationGroup = true, IsExpanded = open,
                ApplicationEntryCount = entries.Length,
                Summary = string.Join(" | ", entries.Select(entry => SourceLabel(entry.SourceKind)).Distinct()),
                ProcessSummary = processes.Length > 0 ? L.F($"匹配 {processes.Length} 个运行进程") : "",
                DetailText = ""
            });
            if (!open) continue;
            foreach (var entry in entries)
            {
                var row = CreateRow(entry, index, expanded.Contains(entry.Id), query, collapsedSearch.Contains(entry.Id));
                row.ApplicationGroupKey = group.Key;
                row.PresentationDepth = 1;
                result.Add(row);
            }
        }
        return result.ToArray();
    }
}
