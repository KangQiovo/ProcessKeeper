using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private IEnumerable<LegacyRow> AutorunRows(string query, int category, int sort,
        HashSet<string> expanded, HashSet<string> collapsed, ProcessSnapshot snapshot,
        IReadOnlyList<InstalledApplication> installed, AutorunSnapshot autoruns,
        bool complex, ApplicationDisplayCatalog display, CancellationToken token)
    {
        var index = new AutorunSearchIndex(snapshot, installed);
        bool Open(string id) => query.Length > 0 ? !collapsed.Contains(id) : expanded.Contains(id);
        foreach (var group in ApplicationPresentationGroups.GroupAutoruns(autoruns.Entries, installed, snapshot)
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var appMatch = ApplicationSearch.Contains(group.Name, query);
            var entries = group.Entries.Where(entry => AutorunPresentation.Matches(entry, complex, category) &&
                !(sort == 1 && entry.Enabled != true || sort == 2 && entry.Enabled != false ||
                    sort == 3 && entry.Enabled.HasValue || sort == 4 && !entry.CanChange) &&
                (query.Length == 0 || appMatch || index.Matches(entry, query) ||
                    MatchesPlatformQuery(query, display.FindGame(entry), display.FindGamePlatform(entry))))
                .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.SourceKind).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
            if (entries.Length == 0) continue;
            var grouped = group.Entries.Count > 1;
            if (grouped)
            {
                yield return new LegacyRow
                {
                    Id = group.Key, Name = group.Name, Path = group.IconPath, IconPath = group.IconPath,
                    Summary = string.Join(" | ", entries.Select(entry => SourceName(entry.SourceKind)).Distinct()),
                    Model = group, Expanded = Open(group.Key)
                };
                if (!Open(group.Key)) continue;
            }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var id = "autorun:" + entry.Id;
                var processes = index.Processes(entry);
                var protectedEntry = entry.Enabled == true && IsAutorunWhitelisted(entry);
                var owners = processes.Select(process => process.ApplicationName).Concat(index.Applications(entry))
                    .Where(name => name.Length > 0).Distinct();
                yield return new LegacyRow
                {
                    Id = id, ParentId = grouped ? group.Key : "", IsChild = grouped,
                    Name = entry.Name, Summary = (protectedEntry ? L.T("白名单保护") + " | " : "") +
                        SourceName(entry.SourceKind) + (entry.Ownership == AutorunOwnership.Windows ? " | " + L.T("系统项") : "") +
                        " | " + (entry.Enabled == true ? L.T("已启用") : entry.Enabled == false ? L.T("已禁用") : L.T("状态未知")) +
                        " | " + string.Join(", ", owners),
                    Detail = entry.Location + "\n" + entry.Command + "\n" + entry.ReadOnlyReason,
                    Path = entry.TargetPath, Model = entry, Expanded = Open(id),
                    HasAction = true, CanAct = !_busy && !protectedEntry, IsActionChecked = entry.Enabled == true,
                    ActionLabel = !entry.CanChange || !entry.Enabled.HasValue ? L.T("查看原因") :
                        entry.Enabled == true ? L.T("禁用此项") : L.T("启用此项")
                };
                if (Open(id)) foreach (var process in processes)
                {
                    var row = ProcessRow(process, process.ApplicationName, process.ApplicationKey, snapshot, id);
                    row.ParentId = id;
                    if (grouped) row.PresentationDepth = 1;
                    yield return row;
                }
            }
        }
    }
}
