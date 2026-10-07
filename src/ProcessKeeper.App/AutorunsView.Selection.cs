using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView
{
    private Func<AutorunEntry, bool>? _whitelistProtection;

    internal void ApplyWhitelistProtection(Func<AutorunEntry, bool>? predicate)
    { _whitelistProtection = predicate; Render(); }

    private bool IsWhitelistProtected(AutorunEntry entry)
    {
        try { return _whitelistProtection?.Invoke(entry) == true; }
        catch { return true; }
    }

    private static bool Selectable(AutorunRow row) => true;

    private void ToggleSelectionClicked(object sender, RoutedEventArgs args)
    {
        var rows = _rows.Where(Selectable).ToArray();
        var clear = rows.Length > 0 && rows.All(EntriesList.SelectedItems.Contains);
        var tree = NativeSelectionTree.For(EntriesList);
        tree?.ClearSelection();
        if (!clear) tree?.SelectRows(rows, true);
        UpdateSelectionToggle();
    }

    private void EntrySelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (NativeSelectionTree.For(EntriesList)?.IsApplying != true) UpdateSelectionToggle(); }

    private void UpdateSelectionToggle()
    {
        if (!_initialized) return;
        var rows = _rows.Where(Selectable).ToArray();
        SelectionToggle.Content = L.T(rows.Length > 0 && rows.All(EntriesList.SelectedItems.Contains) ? "全不选" : "全选");
        SelectionToggle.IsEnabled = !_changing && rows.Length > 0;
    }

    private static AutorunRow[] ExpandProcessRows(IEnumerable<AutorunRow> rows)
    {
        var result = new List<AutorunRow>();
        foreach (var row in rows)
        {
            result.Add(row);
            if (!row.IsExpanded || row.IsPresentationGroup || row.IsApplicationGroup) continue;
            foreach (var process in row.Processes)
                result.Add(new AutorunRow
                {
                    Entry = row.Entry, Process = process, Summary = process.ApplicationName + " | " + (process.MemoryBytes / 1048576d).ToString("0.0") + " MB",
                    DetailText = "", ProcessSummary = process.Path ?? "", PresentationDepth = row.PresentationDepth,
                    PresentationPlatformId = row.PresentationPlatformId
                });
        }
        return result.ToArray();
    }
}
