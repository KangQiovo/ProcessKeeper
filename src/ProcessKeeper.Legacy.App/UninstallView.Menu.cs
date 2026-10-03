using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class UninstallView
{
    private readonly Action<string> _openLocation;
    private readonly Action<string> _copyText;
    private ContextMenu? _rowMenu;
    private Task _menuLocationTask = Task.CompletedTask;

    private void ConfigureFilter()
    {
        _filterReady = false;
        _filter.ItemsSource = (_mode.IsChecked == true
            ? new[] { "全部卸载项", "常规注册项", "隐藏注册项", "建议卸载", "可卸载", "仅查看" }
            : new[] { "全部应用", "系统应用", "第三方应用", "驱动与运行库" }).Select(L.T).ToArray();
        _filter.SelectedIndex = 0; _filterReady = true;
    }

    private void RowContextOpening(object sender, ContextMenuEventArgs args)
    {
        if (_closed || IsBusy) { args.Handled = true; return; }
        var container = ItemsControl.ContainerFromElement(_list, args.OriginalSource as DependencyObject) as ListBoxItem;
        var row = container?.DataContext as Row ?? (args.CursorLeft < 0 ? _list.SelectedItem as Row : null);
        if (row is null) { args.Handled = true; return; }
        if (!_list.SelectedItems.Contains(row)) _list.SelectedItems.Add(row);
        if (row.IsGroup) { args.Handled = true; return; }
        _list.ContextMenu = BuildRowMenu(row);
    }

    private ContextMenu BuildRowMenu(Row row)
    {
        if (row.IsGroup) return new ContextMenu();
        if (_rowMenu is not null) _rowMenu.IsOpen = false;
        var menu = new ContextMenu(); _rowMenu = menu; Wpf.Ui.Appearance.ApplicationThemeManager.Apply(menu);
        var open = new MenuItem { Header = L.T("显示文件所在目录"), IsEnabled = false, ToolTip = L.T("未找到可访问的应用目录。") };
        var copyPath = new MenuItem { Header = L.T("复制路径"), IsEnabled = false };
        var copyName = new MenuItem { Header = L.T("复制名称") };
        copyName.Click += (_, _) => InvokeMenuAction(() => _copyText(row.Entry.Name));
        menu.Items.Add(open); menu.Items.Add(copyPath); menu.Items.Add(copyName); menu.Items.Add(new Separator());
        var normal = new MenuItem { Header = L.T("卸载"), IsEnabled = !IsBusy && _canAct() && !IsWhitelistProtected(row.Entry) && row.Entry.CanUninstall };
        var quiet = new MenuItem { Header = L.T("注册的静默卸载"), IsEnabled = !IsBusy && _canAct() && !IsWhitelistProtected(row.Entry) && row.Entry.CanQuietUninstall };
        normal.Click += async (_, _) => await UninstallFromMenuAsync(row, UninstallMode.Normal);
        quiet.Click += async (_, _) => await UninstallFromMenuAsync(row, UninstallMode.RegisteredQuiet);
        menu.Items.Add(normal); menu.Items.Add(quiet);
        menu.Closed += (_, _) => { if (ReferenceEquals(_rowMenu, menu)) _rowMenu = null; };
        _menuLocationTask = PopulateMenuLocationAsync(menu, row, open, copyPath);
        return menu;
    }

    private async Task PopulateMenuLocationAsync(ContextMenu menu, Row row, MenuItem open, MenuItem copyPath)
    {
        try
        {
            var path = await Task.Run(() => UninstallDisplay.ResolveApplicationLocation(row.Entry), _life.Token);
            if (_closed || !ReferenceEquals(_rowMenu, menu) || path is null) return;
            open.IsEnabled = copyPath.IsEnabled = true; open.ToolTip = path;
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
    private static void OpenApplicationLocation(string path)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Process.Start(new ProcessStartInfo(explorer, Directory.Exists(path) ? "\"" + path.TrimEnd('\\') + "\"" : "/select,\"" + path + "\"") { UseShellExecute = true });
    }
}
