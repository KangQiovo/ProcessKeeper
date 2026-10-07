using System.Windows;
using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private bool _selectingRows, _restoringTreeSelection;
    private readonly SemaphoreSlim _selectionGate = new(1, 1);
    private readonly Dictionary<ListBox, LegacyTreeSelection> _treeSelections = new();
    private int _treeSelectionPage = -1, _treeUniverseVersion = -1;
    private Dictionary<string, string[]>? _treeMembership;
    private async void LegacySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_restoringTreeSelection || _selectingRows || _closed || sender is not ListBox list) return;
        await _selectionGate.WaitAsync();
        try
        {
            if (_closed) return;
            var selection = await PrepareTreeSelectionAsync(list);
            await SyncTreeSelectionAsync(list, selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) Notice(error.Message); }
        finally { _selectionGate.Release(); RefreshSelectionButton(); }
    }
    private async void RowSelectionClicked(object sender, RoutedEventArgs args)
    {
        args.Handled = true;
        if (sender is not FrameworkElement { DataContext: LegacyRow row } element) return;
        await SetRowSelectionAsync(element, row, row.SelectionState != true);
    }
    private async Task SetRowSelectionAsync(FrameworkElement element, LegacyRow row, bool selected)
    {
        var list = Ancestor<ListBox>(element); if (list is null) return;
        await _selectionGate.WaitAsync();
        try
        {
            if (_busy || _closed) { row.NotifySelectionState(); return; }
            var selection = await PrepareTreeSelectionAsync(list);
            selection.Select(row.Id, selected); await SyncTreeSelectionAsync(list, selection);
        }
        catch (OperationCanceledException) { row.NotifySelectionState(); }
        catch (Exception error) { row.NotifySelectionState(); if (!_closed) Notice(error.Message); }
        finally { _selectionGate.Release(); RefreshSelectionButton(); }
    }
    private async Task<LegacyTreeSelection> PrepareTreeSelectionAsync(ListBox list, int attempt = 0)
    {
        bool production = ReferenceEquals(list, List) && _ready;
        if (production && _treeSelectionPage != _page) { _treeSelections.Remove(list); _treeSelectionPage = _page; _treeMembership = null; }
        if (!_treeSelections.TryGetValue(list, out var selection)) _treeSelections[list] = selection = new LegacyTreeSelection();
        if (production && _treeMembership is not null && _treeUniverseVersion == _renderVersion) { selection.Configure(_treeMembership); return selection; }
        IReadOnlyList<LegacyRow> universe;
        if (!production) universe = list.Items.OfType<LegacyRow>().ToArray();
        else
        {
            var page = _page; var snapshot = _snapshot; var installed = AllInstalledApplications(); var autoruns = _autoruns;
            var rules = page == 0 ? RunningRules : page == 1 && !_whitelistScope.Installed ? Array.Empty<WhitelistRule>() : _rules;
            var display = _displayCatalog;
            var expanded = new HashSet<string>(snapshot.Applications.Select(app => "app:" + app.Key)
                .Concat(installed.Select(app => "installed:" + app.Id))
                .Concat(installed.SelectMany(app => app.Executables.Select(exe => "installed:" + app.Id + "|exe:" + exe.Path.ToUpperInvariant())))
                .Concat(rules.Select(rule => "rule:" + rule.Id))
                .Concat(autoruns.Entries.Select(entry => "autorun:" + entry.Id))
                .Concat(ApplicationPresentationGroups.GroupAutoruns(autoruns.Entries, installed, snapshot).Select(group => group.Key)), StringComparer.Ordinal);
            expanded.UnionWith(ApplicationPresentationGroups.GroupRules(rules, installed, snapshot).Select(group => group.Key));
            var group = _view.GroupGamePlatforms;
            var uninstall = _uninstallView?.InventoryEntries.ToArray() ?? Array.Empty<UninstallEntry>(); var version = _renderVersion;
            var membership = await Task.Run(() => LegacyTreeSelection.Membership(ApplyPresentation(CreateRows(page, "", 0, 0, "", true, expanded, snapshot, installed, rules, autoruns, _life.Token, new HashSet<string>(), true, display, uninstall), page, "", display, group, false, new HashSet<string>())), _life.Token);
            if (_closed || page != _page) throw new OperationCanceledException();
            if (version != _renderVersion)
            {
                if (attempt < 2) return await PrepareTreeSelectionAsync(list, attempt + 1);
                throw new OperationCanceledException();
            }
            _treeMembership = membership; _treeUniverseVersion = version; universe = Array.Empty<LegacyRow>();
        }
        selection.Configure(production ? _treeMembership! : LegacyTreeSelection.Membership(universe)); return selection;
    }
    private async Task SyncTreeSelectionAsync(ListBox list, LegacyTreeSelection selection)
    {
        _restoringTreeSelection = true;
        bool hitTest = list.IsHitTestVisible; list.IsHitTestVisible = false;
        try
        {
            var selected = new HashSet<object>(list.SelectedItems.Cast<object>());
            var rows = list.Items.OfType<LegacyRow>().ToArray();
            for (int index = 0; index < rows.Length && !_closed; index++)
            {
                var row = rows[index];
                var state = selection.State(row.Id); row.SetSelectionState(state);
                if (state == true && !selected.Contains(row) && list.Items.Contains(row)) list.SelectedItems.Add(row);
                else if (state != true && selected.Contains(row)) list.SelectedItems.Remove(row);
                row.NotifySelectionState();
                if ((index + 1) % 64 == 0) await Task.Yield();
            }
        }
        finally { _restoringTreeSelection = false; list.IsHitTestVisible = hitTest; }
    }
    private async Task RestoreTreeSelectionAsync()
    {
        if (!_treeSelections.ContainsKey(List)) return;
        await _selectionGate.WaitAsync();
        try { if (!_closed) await SyncTreeSelectionAsync(List, await PrepareTreeSelectionAsync(List)); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) Notice(error.Message); }
        finally { _selectionGate.Release(); }
    }
    private void RefreshSelectionButton()
    {
        if (_selectingRows || _closed) return;
        SelectAllButton.Content = L.T(List.Items.Count > 0 && List.SelectedItems.Count == List.Items.Count ? "全不选" : "全选");
        SelectAllButton.IsEnabled = !_busy && List.Items.Count > 0;
    }
    private async void ToggleAllSelectionClicked(object sender, RoutedEventArgs args)
    {
        if (_busy || _closed || _selectingRows) return;
        _selectingRows = true; List.IsHitTestVisible = false;
        await _selectionGate.WaitAsync();
        try
        {
            var selection = await PrepareTreeSelectionAsync(List);
            bool selected = !(List.Items.Count > 0 && List.SelectedItems.Count == List.Items.Count);
            foreach (var row in List.Items.OfType<LegacyRow>()) selection.Select(row.Id, selected);
            await SyncTreeSelectionAsync(List, selection);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed) Notice(error.Message); }
        finally { _selectionGate.Release(); _selectingRows = false; if (!_closed) { List.IsHitTestVisible = true; RefreshSelectionButton(); } }
    }
}
