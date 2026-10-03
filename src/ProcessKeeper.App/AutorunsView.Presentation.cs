using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView
{
    private const string PlatformRowPrefix = "presentation-platform:";
    private ApplicationDisplayCatalog _displayCatalog = ApplicationDisplayCatalog.Empty;
    private readonly HashSet<string> _collapsedPlatforms = new(StringComparer.Ordinal);
    internal event EventHandler? PresentationChanged;
    internal event EventHandler? InventoryChanged;
    internal bool GroupGamePlatforms => DisplayOptions.GroupGames;
    internal bool HideMicrosoftApps => DisplayOptions.HideMicrosoft;
    internal IReadOnlyList<AutorunEntry> InventoryEntries => _inventory.Entries;
    private static bool MatchesPlatformQuery(AutorunEntry entry, string query, ApplicationDisplayCatalog display) => query.Length > 0 &&
        ((display.FindGame(entry)?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 || (display.FindGamePlatform(entry)?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);

    internal void ApplyPresentation(ApplicationDisplayCatalog catalog, bool group, bool hide)
    { _displayCatalog = catalog; DisplayOptions.Apply(group, hide); Render(); }

    private static AutorunRow[] GroupRows(AutorunRow[] rows, ApplicationDisplayCatalog display, bool group, HashSet<string> collapsed, string query)
    {
        if (!group) return rows;
        var segments = new List<List<AutorunRow>>();
        foreach (var row in rows)
        {
            if (row.ApplicationGroupKey.Length == 0 || row.IsApplicationGroup || segments.Count == 0)
                segments.Add(new List<AutorunRow>());
            segments[^1].Add(row);
        }
        GamePlatform? Platform(AutorunRow row)
        {
            if (!row.IsApplicationGroup) return display.FindGamePlatform(row.Entry);
            var platforms = row.ApplicationEntries.Select(display.FindGamePlatform).ToArray();
            return platforms.Length > 0 && platforms.All(item => item is not null && item.Id == platforms[0]?.Id) ? platforms[0] : null;
        }
        var classified = segments.Select(segment => (Rows: segment, Platform: Platform(segment[0]))).ToArray();
        var result = new List<AutorunRow>();
        foreach (var platform in classified.Where(item => item.Platform is not null).GroupBy(item => item.Platform!.Id))
        {
            var first = platform.First().Platform!; var open = query.Length > 0 || !collapsed.Contains(first.Id);
            result.Add(new AutorunRow { Entry = new AutorunEntry { Id = PlatformRowPrefix + first.Id, Name = first.Name, TargetPath = display.Clients.FirstOrDefault(c => c.Platform.Id == first.Id)?.ExecutablePath ?? "" },
                Summary = L.T("游戏平台"), ProcessSummary = "", DetailText = "", IsExpanded = open,
                IsPresentationGroup = true, PresentationPlatformId = first.Id });
            if (open) foreach (var item in platform) foreach (var row in item.Rows)
            { row.PresentationDepth++; row.PresentationPlatformId = first.Id; result.Add(row); }
        }
        foreach (var item in classified.Where(item => item.Platform is null)) result.AddRange(item.Rows);
        return result.ToArray();
    }
}
