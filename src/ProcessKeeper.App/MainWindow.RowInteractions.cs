using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<DependencyObject, (ListView List, object Row)> _realizedContainers = [];
    private readonly Dictionary<object, IconLease> _visibleIcons = [];
    private const int MaximumRetainedRowIcons = 128;
    private readonly LinkedList<object> _retainedIconOrder = new();
    private readonly Dictionary<object, (string Path, LinkedListNode<object> Node)> _retainedIcons = [];
    private bool _iconRefreshQueued;

    private sealed class IconLease(string path)
    {
        internal string Path { get; } = path;
        internal bool Requested { get; set; }
    }

    private void InitializeRowInteractions()
    {
        InitializeSelectionTrees();
        AppsList.RowInvoked += row => { if (row is AppRow app) ToggleAppRow(app); };
        InstalledList.RowInvoked += row => { if (row is InstalledRow installed) ToggleInstalledRow(installed); };
        RulesList.RowInvoked += row => { if (row is RuleRow rule) ToggleRuleRow(rule); };
        foreach (var list in new[] { AppsList, InstalledList, RulesList })
        {
            // Keep native virtualization. Only realized containers request icons.
            list.ContainerContentChanging += RowContainerChanging;
            list.ContextRequested += RowContextRequested;
        }
    }

    private static void RestoreActionBinding(CheckBox checkbox, string property) => checkbox.SetBinding(CheckBox.IsCheckedProperty,
        new Microsoft.UI.Xaml.Data.Binding { Path = new PropertyPath(property), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });

    private void RowContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is null)
            _realizedContainers.Remove(args.ItemContainer);
        else if (sender is ListView list)
            _realizedContainers[args.ItemContainer] = (list, args.Item);
        QueueVisibleIcons();
    }

    private bool IsActiveList(ListView list) => list == AppsList ? AppsPage.Visibility == Visibility.Visible :
        list == InstalledList ? InstalledPage.Visibility == Visibility.Visible : RulesPage.Visibility == Visibility.Visible;

    private static string? RowIconPath(object row) => row switch
    {
        AppRow app => app.IconPath,
        InstalledRow installed => installed.IconPath,
        RuleRow rule => rule.IconPath,
        _ => null
    };

    private static void SetRowIcon(object row, ImageSource? image)
    {
        switch (row)
        {
            case AppRow app when !ReferenceEquals(app.Icon, image): app.Icon = image; app.NotifyIcon(); break;
            case InstalledRow installed when !ReferenceEquals(installed.Icon, image): installed.Icon = image; installed.NotifyIcon(); break;
            case RuleRow rule when !ReferenceEquals(rule.Icon, image): rule.Icon = image; rule.NotifyIcon(); break;
        }
    }

    private void QueueVisibleIcons()
    {
        if (_closed || _iconRefreshQueued) return;
        _iconRefreshQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _iconRefreshQueued = false;
            if (_closed) return;
            var visible = _realizedContainers.Values.Where(value => IsActiveList(value.List)).Select(value => value.Row).ToHashSet();
            foreach (var old in _retainedIcons.Keys.Where(row => !RowRemainsInActiveList(row)).ToArray())
            { RemoveRetainedIcon(old); SetRowIcon(old, null); }
            foreach (var old in _visibleIcons.Keys.Where(row => !visible.Contains(row)).ToArray())
            {
                var path = _visibleIcons[old].Path;
                _visibleIcons.Remove(old);
                if (RowRemainsInActiveList(old)) RetainRowIcon(old, path);
                else SetRowIcon(old, null);
            }
            foreach (var row in visible)
            {
                var path = RowIconPath(row);
                if (path is null) continue;
                if (!_visibleIcons.TryGetValue(row, out var lease) || !lease.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                {
                    lease = new IconLease(path);
                    _visibleIcons[row] = lease;
                    if (_retainedIcons.TryGetValue(row, out var retained) && retained.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
                    { RemoveRetainedIcon(row); lease.Requested = row switch { AppRow app => app.Icon is not null, InstalledRow installed => installed.Icon is not null, RuleRow rule => rule.Icon is not null, _ => false }; }
                    else { RemoveRetainedIcon(row); SetRowIcon(row, null); }
                }
                if (lease.Requested) continue;
                lease.Requested = true;
                _ = LoadVisibleIconAsync(row, lease);
            }
        });
    }

    private bool RowRemainsInActiveList(object row) => row switch
    {
        AppRow app => AppsPage.Visibility == Visibility.Visible && _appRows.Contains(app),
        InstalledRow installed => InstalledPage.Visibility == Visibility.Visible && _installedRows.Contains(installed),
        RuleRow rule => RulesPage.Visibility == Visibility.Visible && _ruleRows.Contains(rule),
        _ => false
    };
    private void RemoveRetainedIcon(object row)
    { if (_retainedIcons.Remove(row, out var retained)) _retainedIconOrder.Remove(retained.Node); }
    private void RetainRowIcon(object row, string path)
    {
        RemoveRetainedIcon(row);
        _retainedIcons[row] = (path, _retainedIconOrder.AddLast(row));
        while (_retainedIcons.Count > MaximumRetainedRowIcons && _retainedIconOrder.First is { } first)
        { var old = first.Value; RemoveRetainedIcon(old); SetRowIcon(old, null); }
    }

    private async Task LoadVisibleIconAsync(object row, IconLease lease)
    {
        try
        {
            var icon = await _icons.GetAsync(lease.Path);
            if (!_closed && _visibleIcons.TryGetValue(row, out var current) && ReferenceEquals(current, lease))
                SetRowIcon(row, icon);
        }
        catch (IconRequestDeferredException)
        {
            lease.Requested = false; // The next timer tick or realization retries a busy request.
        }
        catch (OperationCanceledException) { lease.Requested = false; }
        catch { /* An unreadable icon must not stop monitoring or mutate any whitelist rule. */ }
    }

    private void RowContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_closed) return;
        object? row = null;
        FrameworkElement? anchor = null;
        for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: AppRow or InstalledRow or RuleRow } framework)
            {
                row = framework.DataContext; anchor = framework; break;
            }
            if (ReferenceEquals(element, sender)) break;
        }
        if (row is null || anchor is null) return;
        args.Handled = true;
        if (row is AppRow { IsPresentationGroup: true } or InstalledRow { IsPresentationGroup: true }) return;
        var path = RowLocation(row);
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = L.T("显示文件所在目录"), Icon = new SymbolIcon(Symbol.OpenFile), IsEnabled = path is not null };
        if (path is not null) open.Click += (_, _) => OpenRowLocation(path);
        else ToolTipService.SetToolTip(open, L.T("未找到可访问的文件路径；未运行的规则可先在已安装应用页扫描软件。"));
        menu.Items.Add(open);
        var process = RowRecoveryProcess(row);
        if (process is not null && SensitiveProcessClose.IsSensitiveComponent(process))
        {
            var close = new MenuFlyoutItem
            {
                Text = L.T("强制关闭敏感进程…"), Icon = new FontIcon { Glyph = "\uE7BA" },
                IsEnabled = !_working && !_windowOperationRunning && !_dialogOpen && _configurationHealthy && !_policy.EvaluateSensitiveClose(process, _snapshot, RunningRules).Protected
            };
            close.Click += async (_, _) => await CloseSensitiveProcessAsync(process);
            menu.Items.Add(close);
        }
        if (process is not null && WindowRecoveryHints.ForProcess(process, _snapshot) is { } hint)
        {
            var recover = new MenuFlyoutItem { Text = hint.Kind == WindowRecoveryKind.HiddenWindow ? L.T("显示隐藏窗口…") : L.T("打开对应页面…"),
                Icon = new SymbolIcon(Symbol.OpenPane), IsEnabled = !_working && !_windowOperationRunning && !_dialogOpen };
            ToolTipService.SetToolTip(recover, hint.Tooltip);
            recover.Click += async (_, _) => await OpenProcessPageAsync(process);
            menu.Items.Add(recover);
        }
        if (args.TryGetPosition(anchor, out var position)) menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(anchor);
    }

    private ProcessRecord? RowRecoveryProcess(object row)
    {
        int? id = row switch { AppRow app => app.ProcessId, InstalledRow installed => installed.ProcessId, RuleRow rule => rule.ProcessId, _ => null };
        if (id is null) return null;
        var process = _snapshot.Processes.FirstOrDefault(p => p.Id == id.Value);
        if (process is null) return null;
        bool matches = row switch
        {
            AppRow app => app.RowKey.EndsWith($"|{process.Id}:{process.StartTimeUtcTicks}", StringComparison.Ordinal),
            InstalledRow installed => installed.ProcessStartTicks == process.StartTimeUtcTicks,
            RuleRow rule => rule.ProcessStartTicks == process.StartTimeUtcTicks,
            _ => false
        };
        return matches ? process : null;
    }

    private string? RowLocation(object row)
    {
        IEnumerable<string> paths = row switch
        {
            AppRow { ProcessId: int id } => _snapshot.Processes.Where(p => p.Id == id).Select(p => p.Path),
            AppRow app => _snapshot.Applications.Where(a => a.Key == app.Key).SelectMany(a => a.Processes)
                .OrderByDescending(p => p.HasVisibleWindow).Select(p => p.Path),
            InstalledRow installed when installed.ExecutablePath.Length > 0 => [installed.ExecutablePath],
            InstalledRow installed => InstalledParentLocation(installed),
            RuleRow rule => new[] { rule.LocationPath }.Concat(_rules.Where(r => r.Id == rule.Id && r.Kind is ProcessKeeper.Core.RuleKind.ExecutablePath or ProcessKeeper.Core.RuleKind.Directory).Select(r => r.Value)),
            _ => []
        };
        return paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path)));
    }

    private IEnumerable<string> InstalledParentLocation(InstalledRow row)
    {
        var application = AllInstalledApplications().FirstOrDefault(a => a.Id == row.ApplicationId);
        if (application is null) return Array.Empty<string>();
        var identity = new InstalledExecutableIdentity(_uninstallView?.InventoryEntries).ResolveMain(application);
        return identity.Status == InstalledMainIdentityStatus.Resolved
            ? new[] { identity.ExecutablePath, application.InstallLocation } : new[] { application.InstallLocation };
    }

    private void OpenRowLocation(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                ShowNotice(L.T("位置已失效"), L.T("软件可能已卸载、移动或路径不可访问，请刷新列表。"), InfoBarSeverity.Informational);
                return;
            }
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer)
            {
                Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowNotice(L.T("无法打开文件位置"), ex.Message, InfoBarSeverity.Warning); }
    }

    private void PageLiveRefreshChanged(object sender, RoutedEventArgs args)
    {
        if (!_ready || _changingViews || sender is not ToggleSwitch toggle || LiveToggle.IsOn == toggle.IsOn) return;
        LiveToggle.IsOn = toggle.IsOn;
        RefreshInstalledRunningState();
    }
}
