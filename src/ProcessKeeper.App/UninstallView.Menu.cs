using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class UninstallView
{
    private readonly Action<string> _openLocation;
    private readonly Action<string> _copyText;
    private MenuFlyout? _rowMenu;
    private Task _menuLocationTask = Task.CompletedTask;

    private void ConfigureFilter()
    {
        _filterReady = false;
        _filter.ItemsSource = (_mode.IsOn
            ? new[] { "全部卸载项", "常规注册项", "隐藏注册项", "建议卸载", "可卸载", "仅查看" }
            : new[] { "全部应用", "系统应用", "第三方应用", "驱动与运行库" }).Select(L.T).ToArray();
        _filter.SelectedIndex = 0; _filterReady = true;
    }

    private void RowContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        if (_closed || IsBusy) return;
        Row? row = null; FrameworkElement? anchor = null;
        for (var at = args.OriginalSource as DependencyObject; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is FrameworkElement { DataContext: Row candidate } element) { row = candidate; anchor = element; break; }
            if (ReferenceEquals(at, _list)) break;
        }
        if (row is null && !args.TryGetPosition(_list, out _) && _list.SelectedItem is Row selected)
        { row = selected; anchor = _list.ContainerFromItem(selected) as FrameworkElement; }
        if (row is null || anchor is null) return;
        args.Handled = true; if (!_list.SelectedItems.Contains(row)) _list.SelectedItems.Add(row);
        if (row.IsGroup) return;
        var menu = BuildRowMenu(row);
        if (args.TryGetPosition(anchor, out var position)) menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(anchor);
    }

    private MenuFlyout BuildRowMenu(Row row)
    {
        if (row.IsGroup) return new MenuFlyout();
        _rowMenu?.Hide();
        var menu = new MenuFlyout(); _rowMenu = menu; ApplyRowMenuTheme(menu);
        var open = new MenuFlyoutItem { Text = L.T("显示文件所在目录"), Icon = new SymbolIcon(Symbol.OpenFile), IsEnabled = false };
        var copyPath = new MenuFlyoutItem { Text = L.T("复制路径"), Icon = new SymbolIcon(Symbol.Copy), IsEnabled = false };
        ToolTipService.SetToolTip(open, L.T("未找到可访问的应用目录。"));
        var copyName = new MenuFlyoutItem { Text = L.T("复制名称"), Icon = new SymbolIcon(Symbol.Copy) };
        copyName.Click += (_, _) => InvokeMenuAction(() => _copyText(row.Entry.Name));
        menu.Items.Add(open); menu.Items.Add(copyPath); menu.Items.Add(copyName); menu.Items.Add(new MenuFlyoutSeparator());
        var normal = new MenuFlyoutItem { Text = L.T("卸载"), Icon = new SymbolIcon(Symbol.Delete), IsEnabled = !IsBusy && _canAct() && !IsWhitelistProtected(row.Entry) && row.Entry.CanUninstall };
        var quiet = new MenuFlyoutItem { Text = L.T("注册的静默卸载"), IsEnabled = !IsBusy && _canAct() && !IsWhitelistProtected(row.Entry) && row.Entry.CanQuietUninstall };
        normal.Click += async (_, _) => await UninstallFromMenuAsync(row, UninstallMode.Normal);
        quiet.Click += async (_, _) => await UninstallFromMenuAsync(row, UninstallMode.RegisteredQuiet);
        menu.Items.Add(normal); menu.Items.Add(quiet); ApplyRowMenuTheme(menu);
        menu.Closed += (_, _) => { if (ReferenceEquals(_rowMenu, menu)) _rowMenu = null; };
        _menuLocationTask = PopulateMenuLocationAsync(menu, row, open, copyPath);
        return menu;
    }

    private void ApplyRowMenuTheme(MenuFlyout menu)
    {
        // The desktop backdrop can keep the system theme while this detached popup uses
        // an app-selected theme. Resolve the native acrylic brush in the presenter instead.
        var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                   xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="MenuFlyoutPresenter">
                <Setter Property="SystemBackdrop" Value="{x:Null}" />
                <Setter Property="Background" Value="{ThemeResource AcrylicInAppFillColorDefaultBrush}" />
            </Style>
            """);
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme)); menu.MenuFlyoutPresenterStyle = style;
        foreach (var item in menu.Items) item.RequestedTheme = ActualTheme;
    }

    private async Task PopulateMenuLocationAsync(MenuFlyout menu, Row row, MenuFlyoutItem open, MenuFlyoutItem copyPath)
    {
        try
        {
            var path = await Task.Run(() => UninstallDisplay.ResolveApplicationLocation(row.Entry), _life.Token);
            if (_closed || !ReferenceEquals(_rowMenu, menu) || path is null) return;
            open.IsEnabled = copyPath.IsEnabled = true; ToolTipService.SetToolTip(open, path);
            open.Click += (_, _) => InvokeMenuAction(() => _openLocation(path));
            copyPath.Click += (_, _) => InvokeMenuAction(() => _copyText(path));
        }
        catch (Exception) { /* An unavailable path leaves navigation disabled. */ }
    }

    private async Task UninstallFromMenuAsync(Row row, UninstallMode mode)
    {
        if (_closed || IsBusy || !_canAct() || row.IsGroup || !_list.Items.Contains(row)) return;
        if (IsWhitelistProtected(row.Entry)) return;
        _list.SelectedItems.Clear(); _list.SelectedItems.Add(row); await UninstallAsync(mode);
    }
    private void InvokeMenuAction(Action action) { if (_closed) return; try { action(); } catch (Exception ex) { _status.Text = ex.Message; } }
    private static void CopyMenuText(string text)
    {
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }
    private static void OpenApplicationLocation(string path)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, Directory.Exists(path) ? "\"" + path.TrimEnd('\\') + "\"" : "/select,\"" + path + "\"") { UseShellExecute = true });
    }
}
