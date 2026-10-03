using System.Windows;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    internal Action<IReadOnlyList<WhitelistRule>>? SaveBulkRules { get; set; }

    private async void SelectAllClicked(object sender, RoutedEventArgs args) => await ChangeBulkSelectionAsync(true);
    private async void DeselectAllClicked(object sender, RoutedEventArgs args) => await ChangeBulkSelectionAsync(false);

    private async Task ChangeBulkSelectionAsync(bool enabled)
    {
        if (!_ready || _closed || _busy || !_rulesReadable || _page > 2 || _page == 1 && _scanningInstalled) return;
        var page = _page; var query = Search.Text.Trim(); var category = Category.SelectedIndex;
        var drive = _view.InstalledDrive; var showSystem = ShowSystem.IsChecked == true;
        var hideMicrosoft = _view.HideMicrosoftApps; var display = _displayCatalog;
        var snapshot = _snapshot; var rules = _rules; var installed = AllInstalledApplications();
        _busy = true; Shell.IsEnabled = false;
        try
        {
            var profiles = new WhitelistProfilesStore(_directory);
            var planned = await Task.Run(() =>
            {
                var state = profiles.Load();
                if (!state.ActiveRules.SequenceEqual(rules)) throw new InvalidOperationException(L.T("白名单已发生变化，请刷新后重试。"));
                return (State: state, Result: BulkSelectionPlan.Create(page, enabled, query, category, drive,
                    showSystem, true, hideMicrosoft, display, snapshot, installed, rules, _life.Token));
            }, _life.Token);
            var result = planned.Result;
            if (_closed || _life.IsCancellationRequested || !ReferenceEquals(_rules, rules)) return;
            if (result.Changed == 0) { Notice(L.T("当前筛选结果没有需要更改的可编辑规则。")); return; }
            await Task.Run(() =>
            {
                _life.Token.ThrowIfCancellationRequested();
                if (SaveBulkRules is { } save) save(result.Rules);
                else profiles.Save(planned.State.ActiveId, result.Rules, planned.State.Revision);
            }, _life.Token);
            if (_closed) return;
            _rules = result.Rules;
            NotifyProfilesRulesChanged();
            var message = L.F($"已{(enabled ? L.T("全选") : L.T("全不选"))}当前筛选结果 | 更改 {result.Changed} 条规则");
            Log(message); Notice(message + (enabled ? "" : "\n" + L.T("系统保护和其他匹配规则仍然生效。")), true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) Notice(ex.Message); }
        finally { _busy = false; if (!_closed) { Shell.IsEnabled = true; await RenderAsync(); } }
    }
}
