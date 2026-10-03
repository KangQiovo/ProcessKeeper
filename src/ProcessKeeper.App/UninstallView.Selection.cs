using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public sealed partial class UninstallView
{
    private readonly Button _selectionToggle = new() { Content = L.T("全选") };
    private Func<UninstallEntry, bool>? _whitelistProtection;
    internal void ApplyWhitelistProtection(Func<UninstallEntry, bool>? predicate)
    { _whitelistProtection = predicate; if (!_closed) _ = RenderAsync(); }
    private bool IsWhitelistProtected(UninstallEntry entry)
    { try { return _whitelistProtection?.Invoke(entry) == true; } catch { return true; } }
    private UninstallEntry[] FilteredEntries() => _snapshot.Entries.Where(entry =>
        UninstallDisplay.MatchesFilter(entry, _mode.IsOn, _filter.SelectedIndex, _displayCatalog) &&
        (!HideMicrosoftApps || !_displayCatalog.IsMicrosoft(entry)) && (UninstallPresentation.Matches(entry, _search.Text.Trim()) ||
        _search.Text.Trim().Length > 0 && ((_displayCatalog.FindGame(entry)?.Name.IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
        (_displayCatalog.FindGamePlatform(entry)?.Name.IndexOf(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase) ?? -1) >= 0))).ToArray();
    private IEnumerable<UninstallEntry> EntriesFor(Row row) => row.IsPlatform
        ? FilteredEntries().Where(entry => _displayCatalog.FindGamePlatform(entry)?.Id == row.Platform?.Id)
        : row.Group?.Entries ?? new[] { row.Entry };
    private UninstallEntry[] SelectedEntries() => (NativeSelectionTree.For(_list) is { } tree
        ? _snapshot.Entries.Where(entry => tree.IsSelected("entry:" + entry.Id + "|" + entry.Registration + "|" + entry.Fingerprint))
        : _list.SelectedItems.Cast<Row>().SelectMany(EntriesFor))
        .GroupBy(entry => entry.Id + "|" + entry.Registration + "|" + entry.Fingerprint, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    private void ToggleSelection()
    {
        if (_closed || IsBusy) return;
        var selectable = _rows.ToArray();
        bool all = selectable.Length > 0 && selectable.All(row => _list.SelectedItems.Contains(row));
        if (all) _list.SelectedItems.Clear();
        else foreach (var row in selectable) if (!_list.SelectedItems.Contains(row)) _list.SelectedItems.Add(row);
        ShowSelection();
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
