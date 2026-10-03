using System.Windows;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class UninstallView
{
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _searchCollapsedGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedPlatforms = new(StringComparer.Ordinal);
    private ApplicationDisplayCatalog _displayCatalog = ApplicationDisplayCatalog.Empty;
    private UninstallSnapshot? _groupSnapshot;
    private IReadOnlyList<UninstallApplicationGroup>? _groupCache;
    internal event EventHandler? PresentationChanged;
    internal event EventHandler? InventoryChanged;
    internal bool GroupGamePlatforms => _groupPlatforms.IsChecked == true;
    internal bool HideMicrosoftApps => _hideMicrosoft.IsChecked == true;
    internal IReadOnlyList<UninstallEntry> InventoryEntries => _snapshot.Entries;
    private UninstallEntry? SelectedEntry => _list.SelectedItem is Row { IsGroup: false } row ? row.Entry : null;

    internal void ApplyPresentation(ApplicationDisplayCatalog catalog, bool group, bool hide)
    {
        bool changed = !ReferenceEquals(_displayCatalog, catalog) || GroupGamePlatforms != group || HideMicrosoftApps != hide;
        _displayCatalog = catalog; _applyingPresentation = true; try { _groupPlatforms.IsChecked = group; _hideMicrosoft.IsChecked = hide; } finally { _applyingPresentation = false; }
        if (changed && !_closed) _ = RenderAsync();
    }

    private sealed record Segment(UninstallApplicationGroup Group, UninstallEntry[] Entries, GamePlatform? Platform);
    private static (IReadOnlyList<UninstallApplicationGroup> Groups, Row[] Rows, int EntryCount) BuildDisplayRows(
        IReadOnlyList<UninstallApplicationGroup> groups, string query, bool advanced, int filter,
        HashSet<string> expanded, HashSet<string> searchCollapsed, ApplicationDisplayCatalog display,
        bool groupPlatforms, bool hideMicrosoft, HashSet<string> collapsedPlatforms)
    {
        var rows = new List<Row>(); var segments = new List<Segment>(); int count = 0;
        foreach (var group in groups)
        {
            var matches = group.Entries.Where(entry => UninstallDisplay.MatchesFilter(entry, advanced, filter, display) &&
                (!hideMicrosoft || !display.IsMicrosoft(entry)) && (UninstallPresentation.Matches(entry, query) ||
                query.Length > 0 && ((display.FindGame(entry)?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (display.FindGamePlatform(entry)?.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))).ToArray();
            count += matches.Length;
            foreach (var platform in matches.GroupBy(entry => groupPlatforms ? display.FindGamePlatform(entry) : null))
                segments.Add(new Segment(group, platform.ToArray(), platform.Key));
        }
        void AddApplication(Segment segment, int depth)
        {
            var group = segment.Group; var matches = segment.Entries;
            if (group.Entries.Count == 1) { rows.Add(new Row(matches[0], depth: depth, iconPath: display.ResolveIconPath(matches[0]))); return; }
            var key = (segment.Platform?.Id ?? "") + "|" + group.Key;
            bool open = query.Length > 0 ? !searchCollapsed.Contains(key) : expanded.Contains(key);
            rows.Add(new Row(matches[0], group with { Entries = matches }, open, matches.Length, depth: depth, expansionKey: key, iconPath: display.ResolveIconPath(matches[0])));
            if (open) foreach (var entry in matches) rows.Add(new Row(entry, depth: depth + 1, iconPath: display.ResolveIconPath(entry)));
        }
        foreach (var bucket in segments.Where(item => item.Platform is not null).GroupBy(item => item.Platform!.Id))
        {
            var platform = bucket.First().Platform!; var key = "platform:" + platform.Id;
            bool open = query.Length > 0 ? !searchCollapsed.Contains(key) : !collapsedPlatforms.Contains(key);
            rows.Add(new Row(new UninstallEntry { Id = key, Name = platform.Name,
                IconPath = display.Clients.FirstOrDefault(client => client.Platform.Id == platform.Id)?.ExecutablePath ?? "" },
                expanded: open, count: bucket.Sum(item => item.Entries.Length), platform: platform, expansionKey: key));
            if (open) foreach (var segment in bucket) AddApplication(segment, 1);
        }
        foreach (var segment in segments.Where(item => item.Platform is null)) AddApplication(segment, 0);
        return (groups, rows.ToArray(), count);
    }

    private void ToggleGroup(Row row)
    {
        if (_closed || IsChanging || _confirming || !row.IsGroup) return;
        if (_search.Text.Trim().Length > 0)
        {
            if (row.Expanded) _searchCollapsedGroups.Add(row.ExpansionKey); else _searchCollapsedGroups.Remove(row.ExpansionKey);
        }
        else if (row.IsPlatform)
        { if (!_collapsedPlatforms.Add(row.ExpansionKey)) _collapsedPlatforms.Remove(row.ExpansionKey); }
        else if (!_expandedGroups.Add(row.ExpansionKey)) _expandedGroups.Remove(row.ExpansionKey);
        _ = RenderAsync();
    }

    private sealed class Row(UninstallEntry entry, UninstallApplicationGroup? group = null, bool expanded = false, int count = 0,
        int depth = 0, GamePlatform? platform = null, string expansionKey = "", string? iconPath = null) : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly PresentationChanges _changes = new();
        private bool _whitelistProtected;
        private bool? _selectionState = false;
        public bool? SelectionState => _selectionState;
        public void SetSelectionState(bool? value)
        {
            if (_selectionState == value) return;
            _selectionState = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectionState)));
        }
        public void NotifySelectionState() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SelectionState)));
        public UninstallEntry Entry => entry;
        public UninstallApplicationGroup? Group => group;
        private int Count => count;
        private int Depth => depth;
        public GamePlatform? Platform => platform;
        public bool IsPlatform => platform is not null;
        public bool IsGroup => Group is not null || IsPlatform;
        public bool Expanded => expanded;
        public string ExpansionKey => expansionKey;
        public string Key => IsGroup ? "group:" + ExpansionKey : "entry:" + Entry.Id + "|" + Entry.Registration + "|" + Entry.Fingerprint;
        public string Name => platform?.Name ?? Group?.Name ?? Entry.Name;
        public string IconPath => iconPath ?? Entry.ApplicationPaths.FirstOrDefault() ?? Entry.IconPath;
        public Wpf.Ui.Controls.SymbolRegular Chevron => Expanded ? Wpf.Ui.Controls.SymbolRegular.ChevronDown12 : Wpf.Ui.Controls.SymbolRegular.ChevronRight12;
        public Visibility ChevronVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;
        public Thickness Indent => new(4 + depth * 22, 8, 4, 8);
        public string Summary => (_whitelistProtected ? L.T("白名单保护") + " | " : "") + (IsPlatform ? L.T("游戏平台") + " | " + L.F($"{count} 个卸载项目") : IsGroup ? L.F($"{count} 个版本或登记") : string.Join(" | ", new[] {
            Entry.IsHidden ? L.T("隐藏注册项") : "", Entry.Version, Entry.Publisher, L.F($"{Entry.Locator.View} 位登记"), Entry.Locator.Hive }.Where(value => value.Length > 0)));
        public string RecommendationLabel => L.T("建议卸载");
        public Visibility RecommendationVisibility => !IsGroup && Entry.IsRecommended ? Visibility.Visible : Visibility.Collapsed;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void SetWhitelistProtected(bool value) => _whitelistProtected = value;
        public void Apply(Row next)
        {
            entry = next.Entry; group = next.Group; expanded = next.Expanded; count = next.Count;
            depth = next.Depth; platform = next.Platform; expansionKey = next.ExpansionKey; iconPath = next.IconPath;
            _whitelistProtected = next._whitelistProtected;
            Notify();
        }
        public void Notify() => _changes.Publish(this, PropertyChanged,
            (nameof(Name), Name), (nameof(IconPath), IconPath), (nameof(IsPlatform), IsPlatform), (nameof(IsGroup), IsGroup),
            (nameof(Expanded), Expanded), (nameof(Chevron), Chevron), (nameof(ChevronVisibility), ChevronVisibility),
            (nameof(Indent), Indent), (nameof(Summary), Summary), (nameof(RecommendationLabel), RecommendationLabel),
            (nameof(RecommendationVisibility), RecommendationVisibility));
    }
}
