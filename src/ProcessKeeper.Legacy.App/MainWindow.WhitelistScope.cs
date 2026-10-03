using System.Windows;
using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private WhitelistScopePreferences _whitelistScope = new();
    private IReadOnlyList<WhitelistRule> RunningRules => _whitelistScope.Running ? _rules : Array.Empty<WhitelistRule>();
    private bool _scopeReadable = true;
    private void InitializeWhitelistScope()
    {
        try { _whitelistScope = new WhitelistScopePreferencesStore(_directory).Load(); }
        catch (Exception error) { _scopeReadable = false; _rulesReadable = false; Notice(L.T("白名单范围无法读取") + " | " + error.Message); }
        BuildWhitelistScope(); RefreshWhitelistActionGuards();
    }
    private void BuildWhitelistScope()
    {
        var panel = new StackPanel();
        var options = new[] { new CheckBox { Content = L.T("运行中的程序"), IsChecked = _whitelistScope.Running },
            new CheckBox { Content = L.T("已安装应用"), IsChecked = _whitelistScope.Installed },
            new CheckBox { Content = L.T("自启动"), IsChecked = _whitelistScope.Autoruns },
            new CheckBox { Content = L.T("应用卸载"), IsChecked = _whitelistScope.Uninstall } };
        foreach (var option in options) { option.Margin = new Thickness(0, 0, 0, 8); panel.Children.Add(option); }
        panel.Children.Add(Text(L.T("已启用的页面会保留匹配程序；内存优化始终处理所有程序。"), 12));
        panel.Children.Add(Button(L.T("保存作用范围"), () =>
        {
            if (_closed || _busy) return;
            try
            {
                var next = new WhitelistScopePreferences(options[0].IsChecked == true, options[1].IsChecked == true, options[2].IsChecked == true, options[3].IsChecked == true);
                new WhitelistScopePreferencesStore(_directory).Save(next); _whitelistScope = next; _scopeReadable = true;
                _rules = new WhitelistStore(_directory).Load(); _rulesReadable = true;
                RefreshWhitelistActionGuards(); _ = RenderAsync(); Log(L.T("白名单作用范围已保存")); Notice(L.T("白名单作用范围已保存"), true);
            }
            catch (Exception error) { _rulesReadable = false; Notice(error.Message); }
        }));
        WhitelistScopeHost.Content = panel;
    }
    private bool IsAutorunWhitelisted(AutorunEntry entry) => !_scopeReadable || _whitelistScope.Autoruns &&
        (!_rulesReadable || WhitelistActionMatcher.Matches(new[] { entry.TargetPath }, _rules, _snapshot, AllInstalledApplications()));
    private bool IsUninstallWhitelisted(UninstallEntry entry) => !_scopeReadable || _whitelistScope.Uninstall &&
        (!_rulesReadable || WhitelistActionMatcher.Matches(entry.ApplicationPaths, _rules, _snapshot, AllInstalledApplications()));
    private void RefreshWhitelistActionGuards() => _uninstallView?.ApplyWhitelistProtection(IsUninstallWhitelisted);
    private WhitelistScopePreferences WhitelistScopeForExport() => new WhitelistScopePreferencesStore(_directory).Load();
    private void ApplyWhitelistScope(WhitelistScopePreferences? value)
    { if (value is null) return; _whitelistScope = value; _scopeReadable = true; BuildWhitelistScope(); RefreshWhitelistActionGuards(); }
}
