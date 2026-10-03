using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private WhitelistScopePreferences _whitelistScope = new();
    private bool _scopeReadable = true;
    private IReadOnlyList<WhitelistRule> RunningRules => _whitelistScope.Running ? _rules : Array.Empty<WhitelistRule>();
    private IReadOnlyList<WhitelistRule> InstalledRulesForScope => _whitelistScope.Installed ? _rules : Array.Empty<WhitelistRule>();
    private void InitializeWhitelistScope()
    {
        try { _whitelistScope = new WhitelistScopePreferencesStore(Path.GetDirectoryName(_store.FilePath)!).Load(); }
        catch (Exception error) { _scopeReadable = false; _configurationHealthy = false; ShowNotice(L.T("白名单范围无法读取"), error.Message, InfoBarSeverity.Error); }
        BuildWhitelistScope(); RefreshWhitelistActionGuards();
    }
    private void BuildWhitelistScope()
    {
        var panel = new StackPanel { Spacing = 10 };
        var options = new[] { new CheckBox { Content = L.T("运行中的程序"), IsChecked = _whitelistScope.Running },
            new CheckBox { Content = L.T("已安装应用"), IsChecked = _whitelistScope.Installed },
            new CheckBox { Content = L.T("自启动"), IsChecked = _whitelistScope.Autoruns },
            new CheckBox { Content = L.T("应用卸载"), IsChecked = _whitelistScope.Uninstall } };
        foreach (var option in options) panel.Children.Add(option);
        panel.Children.Add(new TextBlock { Text = L.T("已启用的页面会保留匹配程序；内存优化始终处理所有程序。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var save = new Button { Content = L.T("保存作用范围") };
        save.Click += (_, _) =>
        {
            if (_closed || _working || _dialogOpen || _windowOperationRunning || _autorunsView?.IsChanging == true || _uninstallView?.IsBusy == true) return;
            try
            {
                var next = new WhitelistScopePreferences(options[0].IsChecked == true, options[1].IsChecked == true, options[2].IsChecked == true, options[3].IsChecked == true);
                new WhitelistScopePreferencesStore(Path.GetDirectoryName(_store.FilePath)!).Save(next); _whitelistScope = next;
                _rules = _store.Load(); _scopeReadable = _configurationHealthy = true;
                RefreshWhitelistActionGuards(); RenderApps(); RenderInstalled(); RenderRules(); Log(L.T("白名单作用范围已保存"));
                ShowNotice(L.T("白名单作用范围已保存"), "", InfoBarSeverity.Success);
            }
            catch (Exception error) { _configurationHealthy = false; ShowNotice(L.T("保存失败"), error.Message, InfoBarSeverity.Error); }
        };
        panel.Children.Add(save); WhitelistScopeHost.Content = panel;
    }
    private WhitelistScopePreferences WhitelistScopeForExport() => new WhitelistScopePreferencesStore(Path.GetDirectoryName(_store.FilePath)!).Load();
    private void ApplyWhitelistScope(WhitelistScopePreferences? value)
    { if (value is null) return; _whitelistScope = value; _scopeReadable = true; BuildWhitelistScope(); RefreshWhitelistActionGuards(); }
    private bool IsAutorunWhitelisted(AutorunEntry entry) => !_scopeReadable || _whitelistScope.Autoruns &&
        (!_configurationHealthy || WhitelistActionMatcher.Matches(new[] { entry.TargetPath }, _rules, _snapshot, AllInstalledApplications()));
    private bool IsUninstallWhitelisted(UninstallEntry entry) => !_scopeReadable || _whitelistScope.Uninstall &&
        (!_configurationHealthy || WhitelistActionMatcher.Matches(entry.ApplicationPaths, _rules, _snapshot, AllInstalledApplications()));
    private void RefreshWhitelistActionGuards()
    { _autorunsView?.ApplyWhitelistProtection(IsAutorunWhitelisted); _uninstallView?.ApplyWhitelistProtection(IsUninstallWhitelisted); }
}
