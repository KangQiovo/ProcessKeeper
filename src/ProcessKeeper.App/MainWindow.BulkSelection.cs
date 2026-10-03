using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    internal Action<IReadOnlyList<WhitelistRule>>? SaveBulkRules { get; set; }
    private bool _selectingRows;
    private void PageSelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (sender is not ListView list || NativeSelectionTree.For(list)?.IsApplying != true) RefreshSelectionButtons(); }
    private void RefreshSelectionButtons()
    {
        if (_selectingRows || _closed) return;
        void Update(Button? button, ListView list)
        {
            if (button is null) return;
            button.Content = L.T(list.Items.Count > 0 && list.SelectedItems.Count == list.Items.Count ? "全不选" : "全选");
            button.IsEnabled = list.Items.Count > 0 && !_working;
        }
        Update(RunningSelectToggleButton, AppsList); Update(InstalledSelectToggleButton, InstalledList); Update(RulesSelectToggleButton, RulesList);
        NativeCommandLayout.Reflow(InstalledCommands); NativeCommandLayout.Reflow(RulesCommands);
    }
    private async void ToggleAllSelectionClicked(object sender, RoutedEventArgs args)
    {
        if (_closed || _working || _selectingRows) return;
        var list = AppsPage.Visibility == Visibility.Visible ? AppsList : InstalledPage.Visibility == Visibility.Visible ? InstalledList : RulesPage.Visibility == Visibility.Visible ? RulesList : null;
        if (list is null) return;
        _selectingRows = true; _rendering = _renderingInstalled = true; list.IsHitTestVisible = false;
        try
        {
            if (list.Items.Count > 0 && list.SelectedItems.Count == list.Items.Count) list.SelectedItems.Clear();
            else
            {
                var items = list.Items.ToArray();
                var selected = list.SelectedItems.ToHashSet();
                for (var index = 0; index < items.Length && !_closed; index++)
                {
                    if (!selected.Contains(items[index]) && list.Items.Contains(items[index])) list.SelectedItems.Add(items[index]);
                    if ((index + 1) % 64 == 0) await Task.Yield();
                }
            }
        }
        finally
        {
            _selectingRows = false; _rendering = _renderingInstalled = false;
            if (!_closed) { list.IsHitTestVisible = true; RefreshSelectionButtons(); UpdateInspectorAvailability(); }
        }
    }

    private void BulkToolbarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (RunningBulkCommands is null) return;
        var compact = args.NewSize.Width < 780;
        Grid.SetColumn(RunningBulkCommands, compact ? 0 : 1);
        Grid.SetRow(RunningBulkCommands, compact ? 1 : 0);
        Grid.SetColumnSpan(RunningPresentationOptions, compact ? 2 : 1);
    }

    private async void SelectAllClicked(object sender, RoutedEventArgs args) => await ChangeBulkSelectionAsync(true);
    private async void DeselectAllClicked(object sender, RoutedEventArgs args) => await ChangeBulkSelectionAsync(false);

    private async Task ChangeBulkSelectionAsync(bool enabled)
    {
        if (!_ready || _closed || _working || _dialogOpen || !_configurationHealthy || _windowOperationRunning || _updatesView?.IsBusy == true) return;
        var page = AppsPage.Visibility == Visibility.Visible ? 0 : InstalledPage.Visibility == Visibility.Visible ? 1 : RulesPage.Visibility == Visibility.Visible ? 2 : -1;
        if (page < 0 || page == 1 && _installedScanning) return;
        var query = (page == 0 ? SearchBox.Text : page == 1 ? InstalledSearch.Text : RulesSearch.Text)?.Trim() ?? "";
        var category = CategoryFilter.SelectedIndex; var drive = _installedDrivePreference;
        var showSystem = ShowSystemToggle.IsChecked == true; var hideMicrosoft = _hideMicrosoftApps;
        var snapshot = _snapshot; var rules = _rules; var installed = AllInstalledApplications().ToArray(); var display = _displayCatalog;
        _working = true;
        Navigation.IsEnabled = PageContent.IsHitTestVisible = false;
        LoadingRing.IsActive = true;
        try
        {
            var profiles = new WhitelistProfilesStore(Path.GetDirectoryName(_store.FilePath)!);
            var planned = await Task.Run(() =>
            {
                var state = profiles.Load();
                if (!state.ActiveRules.SequenceEqual(rules)) throw new InvalidOperationException(L.T("白名单已发生变化，请刷新后重试。"));
                return (State: state, Result: BulkSelectionPlan.Create(page, enabled, query, category, drive,
                    showSystem, false, hideMicrosoft, display, snapshot, installed, rules, _installedLifetime.Token));
            }, _installedLifetime.Token);
            var result = planned.Result;
            if (_closed || _installedLifetime.IsCancellationRequested || !ReferenceEquals(_rules, rules)) return;
            if (result.Changed == 0) { ShowNotice(L.T("批量选择"), L.T("当前筛选结果没有需要更改的可编辑规则。"), InfoBarSeverity.Informational); return; }
            await Task.Run(() =>
            {
                _installedLifetime.Token.ThrowIfCancellationRequested();
                if (SaveBulkRules is { } save) save(result.Rules);
                else profiles.Save(planned.State.ActiveId, result.Rules, planned.State.Revision);
            }, _installedLifetime.Token);
            if (_closed) return;
            _rules = result.Rules;
            NotifyProfilesRulesChanged();
            _lastDetailSignature = "";
            var message = L.F($"已{(enabled ? L.T("全选") : L.T("全不选"))}当前筛选结果 | 更改 {result.Changed} 条规则");
            Log(message);
            ShowNotice(L.T("批量选择"), message + (enabled ? "" : "\n" + L.T("系统保护和其他匹配规则仍然生效。")), InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) ShowNotice(L.T("保存失败"), ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _working = false;
            if (!_closed)
            {
                Navigation.IsEnabled = PageContent.IsHitTestVisible = true; LoadingRing.IsActive = false;
                RenderApps(); RenderInstalled(); RenderRules();
            }
        }
    }
}
