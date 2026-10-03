using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private bool _syncEmptyProfileRules = true;
    private void NotifyProfilesRulesChanged()
    {
        RefreshWhitelistActionGuards();
        if (!_closed && ProfilesHost.Content is WhitelistProfilesView profiles) _ = profiles.NotifyRulesChangedAsync();
    }
    private void LoadProfilesView(object sender, RoutedEventArgs args) => EnsureProfilesView();

    private void EnsureProfilesView()
    {
        if (ProfilesHost.Content is not null) return;
        var store = new WhitelistProfilesStore(Path.GetDirectoryName(_store.FilePath)!);
        ProfilesHost.Content = new WhitelistProfilesView(store,
            () => !_closed && !_working && !_dialogOpen && _configurationHealthy && !_windowOperationRunning && _updatesView?.IsBusy != true,
            async (title, message) => await ShowDialog(NewDialog(title,
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, L.T("确认"))) == ContentDialogResult.Primary,
            async mutation =>
            {
                if (_closed || _working || _dialogOpen || !_configurationHealthy || _windowOperationRunning || _updatesView?.IsBusy == true)
                    throw new InvalidOperationException(L.T("请等待当前操作完成"));
                _working = true;
                try { return await Task.Run(mutation); }
                finally { _working = false; }
            },
            state =>
            {
                if (_closed) return;
                _rules = state.ActiveRules; RefreshWhitelistActionGuards(); _lastDetailSignature = ""; InvalidateRuleSearchIndex(); RenderApps(); RefreshInstalledRunningState();
                Log(L.T("白名单配置已保存") + " | " + state.ActiveProfile.Name);
            }, _syncEmptyProfileRules, enabled =>
            {
                _viewStore.Save(CurrentView with { SyncEmptyProfileRules = enabled });
                _syncEmptyProfileRules = enabled;
            });
    }
}
