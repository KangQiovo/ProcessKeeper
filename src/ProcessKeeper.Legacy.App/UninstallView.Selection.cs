using System.Windows.Controls;
using System.Windows;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public sealed partial class UninstallView
{
    private readonly Button _selectionToggle = new() { Content = L.T("全选") };
    private readonly LegacyTreeSelection _treeSelection = new();
    private bool _restoringSelection;
    private Func<UninstallEntry, bool>? _whitelistProtection;
    internal void ApplyWhitelistProtection(Func<UninstallEntry, bool>? predicate)
    { _whitelistProtection = predicate; if (!_closed) _ = RenderAsync(); }
    private bool IsWhitelistProtected(UninstallEntry entry)
    { try { return _whitelistProtection?.Invoke(entry) == true; } catch { return true; } }
    private UninstallEntry[] FilteredEntries() => _snapshot.Entries.Where(entry =>
        UninstallDisplay.MatchesFilter(entry, _mode.IsChecked == true, _filter.SelectedIndex, _displayCatalog) &&
        (!HideMicrosoftApps || !_displayCatalog.IsMicrosoft(entry)) && (UninstallPresentation.Matches(entry, _search.Text.Trim()) ||
        _search.Text.Trim().Length > 0 && ((_displayCatalog.FindGame(entry)?.Name.IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
        (_displayCatalog.FindGamePlatform(entry)?.Name.IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))).ToArray();
    private IEnumerable<UninstallEntry> EntriesFor(Row row)
    {
        if (row.IsPlatform) return _snapshot.Entries.Where(entry => _displayCatalog.FindGamePlatform(entry)?.Id == row.Platform?.Id);
        if (row.Group is null) return new[] { row.Entry };
        var entries = (_groupCache ?? UninstallDisplay.Group(_snapshot.Entries)).FirstOrDefault(group => group.Key == row.Group.Key)?.Entries ?? row.Group.Entries;
        if (!GroupGamePlatforms) return entries;
        var separator = row.ExpansionKey.IndexOf('|');
        var platformId = separator >= 0 ? row.ExpansionKey.Substring(0, separator) : "";
        return entries.Where(entry => (_displayCatalog.FindGamePlatform(entry)?.Id ?? "") == platformId);
    }
    private static string EntrySelectionKey(UninstallEntry entry) => "entry:" + entry.Id + "|" + entry.Registration + "|" + entry.Fingerprint;
    private UninstallEntry[] SelectedEntries() => _snapshot.Entries.Where(entry => _treeSelection.State(EntrySelectionKey(entry)) == true)
        .GroupBy(EntrySelectionKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    private void ConfigureTreeSelection()
    {
        var members = _snapshot.Entries.GroupBy(EntrySelectionKey, StringComparer.Ordinal).ToDictionary(group => group.Key, group => new[] { group.Key }, StringComparer.Ordinal);
        foreach (var row in _rows) members[row.Key] = EntriesFor(row).Select(EntrySelectionKey).Distinct(StringComparer.Ordinal).ToArray();
        _treeSelection.Configure(members);
    }
    private void UninstallSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringSelection || _closed) return;
        ConfigureTreeSelection();
        SyncTreeSelection(); ShowSelection();
    }
    private void SelectionCheckboxClicked(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement { DataContext: Row row } || _closed) return;
        if (!IsBusy) { ConfigureTreeSelection(); _treeSelection.Select(row.Key, row.SelectionState != true); }
        SyncTreeSelection(); row.NotifySelectionState(); ShowSelection();
    }
    private void SyncTreeSelection()
    {
        _restoringSelection = true;
        try
        {
            var selected = new HashSet<object>(_list.SelectedItems.Cast<object>());
            foreach (var row in _rows)
            {
                var state = _treeSelection.State(row.Key); row.SetSelectionState(state);
                if (state == true && !selected.Contains(row)) _list.SelectedItems.Add(row);
                else if (state != true && selected.Contains(row)) _list.SelectedItems.Remove(row);
            }
        }
        finally { _restoringSelection = false; }
    }
    private void ToggleSelection()
    {
        if (_closed || IsBusy) return;
        ConfigureTreeSelection(); var selectable = _rows.ToArray();
        bool all = selectable.Length > 0 && selectable.All(row => _list.SelectedItems.Contains(row));
        foreach (var row in selectable) _treeSelection.Select(row.Key, !all);
        SyncTreeSelection(); ShowSelection();
    }
    private async Task UninstallSelectedAsync(UninstallEntry[] entries)
    {
        if (_closed || IsBusy || !_canAct()) return;
        _confirming = _batchRunning = true; _operation = CancellationTokenSource.CreateLinkedTokenSource(_life.Token); SetBusy();
        try
        {
            var result = await new UninstallBatch(_backend).RunSelectedAsync(new UninstallSnapshot { Entries = entries }, _confirm,
                () => !_closed && _canAct(), () => { _confirming = false; IsChanging = true; ChangingChanged?.Invoke(true); SetBusy(); },
                _operation.Token, entry => !IsWhitelistProtected(entry));
            if (!_closed) { _status.Text = result.Summary; _details.Text = result.Details; _log(L.T("批量卸载") + " | " + result.Details); }
        }
        catch (Exception ex) { if (!_closed) { _status.Text = ex.Message; _log(ex.Message); } }
        finally { var changing = IsChanging; IsChanging = _confirming = _batchRunning = false; _operation?.Dispose(); _operation = null; if (changing) ChangingChanged?.Invoke(false); if (!_closed) SetBusy(); }
    }
}
