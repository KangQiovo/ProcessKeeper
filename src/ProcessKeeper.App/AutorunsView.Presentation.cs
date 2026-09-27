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
        var classified = rows.Select(row => (Row: row, Platform: display.FindGamePlatform(row.Entry))).ToArray();
        var result = new List<AutorunRow>();
        foreach (var platform in classified.Where(item => item.Platform is not null).GroupBy(item => item.Platform!.Id))
        {
            var first = platform.First().Platform!; var open = query.Length > 0 || !collapsed.Contains(first.Id);
            result.Add(new AutorunRow { Entry = new AutorunEntry { Id = PlatformRowPrefix + first.Id, Name = first.Name, TargetPath = display.Clients.FirstOrDefault(c => c.Platform.Id == first.Id)?.ExecutablePath ?? "" },
                Summary = L.T("游戏平台"), ProcessSummary = "", DetailText = "", IsExpanded = open,
                IsPresentationGroup = true, PresentationPlatformId = first.Id });
            if (open) foreach (var item in platform)
            { item.Row.PresentationDepth = 1; item.Row.PresentationPlatformId = first.Id; result.Add(item.Row); }
        }
        result.AddRange(classified.Where(item => item.Platform is null).Select(item => item.Row));
        return result.ToArray();
    }
}
