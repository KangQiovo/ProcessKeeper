using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;
using Windows.ApplicationModel.DataTransfer;

namespace ProcessKeeper.App;

public sealed partial class AutorunsView : UserControl
{
    internal bool IsChanging => _changing;
    private readonly ObservableCollection<AutorunRow> _rows = [];
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedSearch = new(StringComparer.Ordinal);
    private readonly Dictionary<DependencyObject, AutorunRow> _realized = [];
    private readonly HashSet<AutorunRow> _loadingIcons = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AutorunSnapshot _inventory = new();
    private ProcessSnapshot _processes = ProcessSnapshot.Empty;
    private IReadOnlyList<InstalledApplication> _installed = [];
    private CancellationTokenSource? _scanCancellation;
    private ProcessIconService? _icons;
    private Action<string>? _log;
    private Action<string>? _openLocation;
    private Action<string, string, InfoBarSeverity>? _notice;
    private Func<ContentDialog, Task<ContentDialogResult>>? _showDialog;
    private bool _initialized, _scanning, _changing, _active, _closed, _rendering, _loaded;
    private long _revision;

    // Injectable boundaries keep native UI tests away from real startup configuration.
    internal Func<CancellationToken, AutorunSnapshot> ScanInventory { get; set; } = token => new AutorunCatalog().Scan(token);
    internal Func<AutorunEntry, bool, CancellationToken, AutorunChangeResult> ChangeConfiguration { get; set; } = (entry, enabled, token) => new AutorunManager().ChangeEnabled(entry, enabled, token);

    public AutorunsView()
    {
        InitializeComponent();
        EntriesList.ItemsSource = _rows;
        NativeSelectionTree.Attach(EntriesList, row => ((AutorunRow)row).RowKey, SelectionNodes, UpdateSelectionToggle);
        DisplayOptions.Apply(true, false);
        DisplayOptions.Changed += (_, _) => { PresentationChanged?.Invoke(this, EventArgs.Empty); Render(); };
        SetSources();
        StateFilter.ItemsSource = new[] { L.T("所有状态"), L.T("已启用"), L.T("已禁用"), L.T("状态未知"), L.T("可以更改"), L.T("仅查看") };
        SourceFilter.SelectedIndex = StateFilter.SelectedIndex = 0;
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); Render(); };
        _initialized = true;
    }

    internal void Connect(ProcessIconService icons, Action<string> log, Action<string> openLocation,
        Action<string, string, InfoBarSeverity> notice, Func<ContentDialog, Task<ContentDialogResult>> showDialog)
    { _icons = icons; _log = log; _openLocation = openLocation; _notice = notice; _showDialog = showDialog; }

    internal void StartPreload() { if (!_closed && !_loaded && !_scanning) _ = ScanAsync(); }
    internal void SetActive(bool active)
    {
        _active = active;
        _revision++;
        if (active) { Render(); StartPreload(); }
        else { foreach (var row in _rows) { row.Icon = null; row.NotifyIcon(); } }
    }
    internal void UpdateProcesses(ProcessSnapshot processes)
    { _processes = processes; if (_active && !_changing) Render(); }
    internal void UpdateInstalled(IReadOnlyList<InstalledApplication> installed)
    { _installed = installed; if (_active && !_changing) Render(); }
    internal void Close()
    { _closed = true; _searchTimer.Stop(); _lifetime.Cancel(); _scanCancellation?.Cancel(); _revision++; _realized.Clear(); }

    private async void ScanClicked(object sender, RoutedEventArgs args) => await ScanAsync();
    private void CancelScanClicked(object sender, RoutedEventArgs args) => _scanCancellation?.Cancel();
    private async Task ScanAsync()
    {
        if (_scanning || _closed || _changing) return;
        _scanning = true;
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _scanCancellation.Token;
        SetScanState();
        try
        {
            var inventory = await Task.Run(() => ScanInventory(token), token);
            if (_closed || token.IsCancellationRequested) return;
            _inventory = inventory;
            InventoryChanged?.Invoke(this, EventArgs.Empty);
            _loaded = true;
            _expanded.IntersectWith(inventory.Entries.Select(e => e.Id).Concat(
                ApplicationPresentationGroups.GroupAutoruns(inventory.Entries, _installed, _processes).Select(group => group.Key)));
            Render();
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = L.T("扫描已取消，保留上次结果。"); }
        catch (Exception ex) { if (!_closed) _notice?.Invoke(L.T("自启动扫描失败"), ex.Message, InfoBarSeverity.Warning); }
        finally
        {
            _scanning = false;
            _scanCancellation.Dispose(); _scanCancellation = null;
            if (!_closed) { SetScanState(); if (_loaded) UpdateStatus(); }
        }
    }

    private void SetScanState()
    {
        ScanProgress.IsActive = _scanning;
        ScanButton.IsEnabled = !_scanning && !_changing;
        CancelScanButton.Visibility = _scanning ? Visibility.Visible : Visibility.Collapsed;
        if (_scanning) Status.Text = L.T("正在后台读取登录项、任务和服务…");
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    { if (!_initialized || _closed) return; _collapsedSearch.Clear(); _revision++; _searchTimer.Stop(); _searchTimer.Start(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs args) { if (_initialized) Render(); }
    private void SetSources()
    {
        SourceFilter.ItemsSource = ComplexMode.IsOn
            ? new[] { L.T("所有来源"), L.T("注册表登录项"), L.T("启动文件夹"), L.T("计划任务"), L.T("服务"), L.T("驱动"), L.T("高级系统入口"), L.T("WMI 订阅"), L.T("打包应用启动项") }
            : new[] { L.T("全部启动项"), L.T("常规启动项"), L.T("隐藏启动项") };
        SourceFilter.SelectedIndex = 0;
    }
    private void ModeChanged(object sender, RoutedEventArgs args) { if (_initialized) { SetSources(); Render(); } }
    private void Render()
    {
        _revision++;
        if (!_active || _closed || _rendering) return;
        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        _rendering = true;
        try
        {
            long revision;
            do
            {
                revision = _revision;
                var entries = _inventory.Entries;
                var snapshot = _processes;
                var installed = _installed;
                var query = Search.Text?.Trim() ?? "";
                var source = SourceFilter.SelectedIndex;
                var state = StateFilter.SelectedIndex;
                var complex = ComplexMode.IsOn;
                var display = _displayCatalog; var groupPlatforms = GroupGamePlatforms; var hideMicrosoft = HideMicrosoftApps;
                var collapsedPlatforms = _collapsedPlatforms.ToHashSet(StringComparer.Ordinal);
                var expanded = _expanded.ToHashSet(StringComparer.Ordinal);
                var collapsedSearch = _collapsedSearch.ToHashSet(StringComparer.Ordinal);
                var rows = await Task.Run(() =>
                {
                    var index = new AutorunSearchIndex(snapshot, installed);
                    var items = ApplicationRows(ApplicationPresentationGroups.GroupAutoruns(entries, installed, snapshot),
                        index, query, source, state, complex, display, hideMicrosoft, expanded, collapsedSearch);
                    return ExpandProcessRows(GroupRows(items, display, groupPlatforms, collapsedPlatforms, query));
                }, _lifetime.Token);
                if (_closed || !_active) return;
                if (revision != _revision) continue;
                var keys = rows.Select(row => row.RowKey).ToHashSet(StringComparer.Ordinal);
                for (var i = _rows.Count - 1; i >= 0; i--)
                    if (!keys.Contains(_rows[i].RowKey)) { _rows[i].Icon = null; _rows.RemoveAt(i); }
                var existing = _rows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
                for (var i = 0; i < rows.Length; i++)
                {
                    if (_closed || !_active || revision != _revision) break;
                    var next = rows[i];
                    if (existing.TryGetValue(next.RowKey, out var row))
                    {
                        if (!ReferenceEquals(_rows[i], row)) NativeListSelection.Move(EntriesList, _rows, _rows.IndexOf(row), i, item => item.RowKey);
                        if (!string.Equals(row.IconPath, next.IconPath, StringComparison.OrdinalIgnoreCase))
                        { row.Icon = null; row.NotifyIcon(); }
                        row.Entry = next.Entry;
                        row.ApplicationEntries = next.ApplicationEntries; row.ApplicationGroupKey = next.ApplicationGroupKey;
                        row.ApplicationIconPath = next.ApplicationIconPath; row.IsApplicationGroup = next.IsApplicationGroup;
                        row.ApplicationEntryCount = next.ApplicationEntryCount;
                        row.Process = next.Process; row.Processes = next.Processes;
                        row.Summary = next.Summary; row.ProcessSummary = next.ProcessSummary;
                        row.DetailText = next.DetailText; row.IsExpanded = next.IsExpanded;
                        row.IsPresentationGroup = next.IsPresentationGroup; row.PresentationDepth = next.PresentationDepth; row.PresentationPlatformId = next.PresentationPlatformId;
                    }
                    else { row = next; _rows.Insert(i, row); }
                    row.IsWhitelistProtected = !row.IsApplicationGroup && !row.IsPresentationGroup && IsWhitelistProtected(row.Entry);
                    if (row.IsWhitelistProtected && row.Entry.Enabled == true) row.Summary = L.T("白名单保护") + " | " + row.Summary;
                    row.Busy = _changing;
                    row.Notify();
                    if ((i + 1) % 64 == 0) await Task.Yield();
                }
                if (_closed || !_active) return;
                EmptyText.Text = _loaded ? L.T("没有符合条件的自启动项目") : L.T("正在读取自启动入口…");
                EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                UpdateStatus();
                NativeSelectionTree.For(EntriesList)?.Invalidate();
                UpdateSelectionToggle();
                LoadRealizedIcons();
            } while (!_closed && _active && revision != _revision);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) _notice?.Invoke(L.T("自启动列表读取失败"), ex.Message, InfoBarSeverity.Warning); }
        finally { _rendering = false; }
    }

    private static bool StateMatches(AutorunEntry entry, int selected) => selected switch
    { 1 => entry.Enabled == true, 2 => entry.Enabled == false, 3 => entry.Enabled is null, 4 => entry.CanChange, 5 => !entry.CanChange, _ => true };
    internal static string SourceLabel(AutorunSourceKind kind) => kind switch
    {
        AutorunSourceKind.RegistryRun => L.T("注册表登录项"), AutorunSourceKind.RegistryRunOnce => L.T("一次性登录项"),
        AutorunSourceKind.PolicyRun => L.T("策略登录项"), AutorunSourceKind.StartupFolder => L.T("启动文件夹"),
        AutorunSourceKind.ScheduledTask => L.T("计划任务"), AutorunSourceKind.Service => L.T("服务"),
        AutorunSourceKind.Driver => L.T("驱动"), AutorunSourceKind.AdvancedRegistry => L.T("高级系统入口"),
        AutorunSourceKind.WmiSubscription => L.T("WMI 订阅"), AutorunSourceKind.PackagedStartup => L.T("打包应用启动项"), _ => L.T("未知")
    };
    private static string StateLabel(AutorunEntry entry) => entry.Enabled is null ? L.T("状态未知") : entry.Enabled.Value ? L.T("已启用") : L.T("已禁用");

    private static AutorunRow CreateRow(AutorunEntry entry, AutorunSearchIndex index, bool expanded, string query, bool collapsedSearch)
    {
        var processes = index.Processes(entry);
        var apps = index.Applications(entry);
        var matches = query.Length > 0 ? processes.Where(p => ApplicationSearch.MatchesProcess(p, query)).ToArray() : [];
        expanded |= matches.Length > 0 && !collapsedSearch;
        var orderedProcesses = matches.Concat(processes).DistinctBy(p => p.Id).ToArray();
        var summary = string.Join(" | ", new[] { SourceLabel(entry.SourceKind), StateLabel(entry), entry.Scope,
            entry.IsHidden ? L.T("隐藏任务") : "", entry.Ownership == AutorunOwnership.Windows ? L.T("系统项") : "" }.Where(text => text.Length > 0));
        var detail = expanded ? BuildDetails(entry, apps, orderedProcesses) : "";
        return new AutorunRow
        {
            Entry = entry, Summary = summary, IsExpanded = expanded, DetailText = detail, Processes = orderedProcesses,
            ProcessSummary = processes.Count > 0 ? L.F($"匹配 {processes.Count} 个运行进程") + " | " + string.Join(" | ", orderedProcesses.Take(3).Select(p => $"{p.ApplicationName} ({p.Id})")) : apps.Count > 0 ? L.F($"所属软件：{string.Join(" | ", apps)}") : ""
        };
    }

    private static string BuildDetails(AutorunEntry entry, IReadOnlyList<string> apps, IReadOnlyList<ProcessRecord> processes)
    {
        var detail = L.F($"来源位置：{entry.Location}") + "\n" + L.F($"启动命令：{entry.Command}");
        if (entry.TargetPath.Length > 0) detail += "\n" + L.F($"目标文件：{entry.TargetPath}");
        if (entry.TriggerSummary.Length > 0) detail += "\n" + L.F($"触发条件：{entry.TriggerSummary}");
        if (entry.Details.Length > 0) detail += "\n" + entry.Details;
        if (apps.Count > 0) detail += "\n" + L.F($"所属软件：{string.Join(" | ", apps)}");
        if (processes.Count > 0)
            detail += "\n\n" + L.T("文件路径或服务身份匹配的运行进程：") + "\n" +
                string.Join("\n", processes.Take(100).Select(p => $"{p.ApplicationName} | {p.Name} | PID {p.Id}")) + "\n" +
                L.T("进程匹配不代表本次由此入口启动；共享脚本宿主不会仅凭文件名关联。");
        return detail;
    }

    private void UpdateStatus()
    {
        if (_scanning) return;
        var entryCount = _rows.Where(row => row.IsApplicationGroup).Sum(row => row.ApplicationEntryCount) +
            _rows.Count(row => !row.IsPresentationGroup && !row.IsApplicationGroup && !row.IsProcess && row.ApplicationGroupKey.Length == 0);
        Status.Text = _loaded ? L.F($"显示 {entryCount} / {_inventory.Entries.Count} 项 | {_inventory.CapturedAt.LocalDateTime:HH:mm:ss}") : L.T("尚未完成扫描");
        if (_inventory.Warnings.Count > 0 || _inventory.Truncated) Status.Text += L.T(" | 部分来源未完整读取");
    }

    private void ExpandEntryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: AutorunRow row } || row.IsProcess || _changing) return;
        if (row.IsPresentationGroup)
        {
            if (!_collapsedPlatforms.Add(row.PresentationPlatformId)) _collapsedPlatforms.Remove(row.PresentationPlatformId);
            Render(); return;
        }
        row.IsExpanded = !row.IsExpanded;
        if (row.IsExpanded) { _expanded.Add(row.Entry.Id); _collapsedSearch.Remove(row.Entry.Id); }
        else { _expanded.Remove(row.Entry.Id); _collapsedSearch.Add(row.Entry.Id); }
        row.Notify();
        Render();
    }

    private async void ChangeEntryClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: AutorunRow row } || !row.CanAct || _changing || _scanning || _closed || _showDialog is null) return;
        var entry = row.Entry;
        if (!row.CanChange)
        {
            await _showDialog(new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
                Title = L.T("查看原因"), CloseButtonText = L.T("关闭"),
                Content = new TextBlock { Text = entry.Name + "\n\n" + (entry.ReadOnlyReason.Length > 0 ? entry.ReadOnlyReason : L.T("此入口仅供查看，未修改任何配置。")), TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } });
            return;
        }
        var enable = entry.Enabled == false;
        if (!enable && IsWhitelistProtected(entry)) return;
        var attempted = false;
        _changing = true;
        foreach (var visible in _rows) { visible.Busy = true; visible.Notify(); }
        SetScanState();
        try
        {
            var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
            panel.Children.Add(new TextBlock { Text = entry.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = SourceLabel(entry.SourceKind) + " | " + entry.Scope + "\n" + entry.Location + "\n" + entry.Command, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            panel.Children.Add(new TextBlock { Text = L.T("仅更改此入口。不会立即运行、停止或重启程序；同一软件的其他入口保持不变。"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = enable ? L.T("恢复后将在下一次满足触发条件时生效。") : L.T("确认后先保存恢复信息，再禁用此入口。后台功能可能在下次触发时不可用。"), TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = enable ? L.T("启用自启动项目？") : L.T("禁用自启动项目？"),
                Content = new ScrollViewer { Content = panel, MaxHeight = 380, HorizontalScrollMode = ScrollMode.Disabled },
                PrimaryButtonText = enable ? L.T("确认启用") : L.T("确认禁用"), CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close
            };
            if (await _showDialog(dialog) != ContentDialogResult.Primary || _closed || !enable && IsWhitelistProtected(entry)) return;
            attempted = true;
            var result = await Task.Run(() => !enable && IsWhitelistProtected(entry)
                ? new AutorunChangeResult(false, L.T("白名单保护"))
                : ChangeConfiguration(entry, enable, _lifetime.Token), _lifetime.Token);
            var message = L.F($"自启动 | {entry.Name} | {SourceLabel(entry.SourceKind)} | {result.Message}");
            _log?.Invoke(message);
            if (_closed) return;
            _notice?.Invoke(result.Success ? L.T("自启动设置已更新") : L.T("自启动更改未确认"), result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            _log?.Invoke(L.F($"自启动更改失败：{entry.Name} | {ex.Message}"));
            if (!_closed) _notice?.Invoke(L.T("自启动更改失败"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _changing = false;
            if (!_closed)
            {
                foreach (var visible in _rows) { visible.Busy = false; visible.Notify(); }
                SetScanState();
                if (attempted) await ScanAsync();
            }
        }
    }

    private async void CoverageClicked(object sender, RoutedEventArgs args)
    {
        if (_showDialog is null || _closed) return;
        var text = L.T("读取注册表登录项、启动文件夹、包含隐藏项目的计划任务、自动及触发服务、驱动和高级系统入口。任务也可能按时间或事件触发，并非全部开机执行。") + "\n\n" +
            L.T("高级系统入口和无法确认状态的项目仅供查看。无法访问的来源、未加载用户配置及读取上限可能导致遗漏；本页不保证找出所有启动机制。") + "\n\n" +
            (_inventory.Warnings.Count == 0 ? L.T("本次扫描未报告额外读取问题。") : string.Join("\n", _inventory.Warnings.Take(100)));
        await _showDialog(new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = L.T("扫描详情"), CloseButtonText = L.T("关闭"),
            Content = new ScrollViewer { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 520 }, MaxHeight = 400 }
        });
    }

    private void EntryContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        FrameworkElement? anchor = null;
        for (var node = args.OriginalSource as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        { if (node is FrameworkElement { DataContext: AutorunRow } element) { anchor = element; break; } if (ReferenceEquals(node, sender)) break; }
        if (anchor?.DataContext is not AutorunRow row) return;
        args.Handled = true;
        if (row.IsPresentationGroup) return;
        var menu = new MenuFlyout();
        var path = row.IconPath;
        var open = new MenuFlyoutItem { Text = L.T("显示文件所在目录"), Icon = new SymbolIcon(Symbol.OpenFile), IsEnabled = Path.IsPathFullyQualified(path) && File.Exists(path) };
        open.Click += (_, _) => _openLocation?.Invoke(path);
        menu.Items.Add(open);
        var copy = new MenuFlyoutItem { Text = L.T("复制启动信息"), Icon = new SymbolIcon(Symbol.Copy) };
        copy.Click += (_, _) => { try { var data = new DataPackage(); data.SetText(row.Name + "\n" + (row.IsApplicationGroup
            ? string.Join("\n\n", row.ApplicationEntries.Select(entry => BuildDetails(entry, [], [])))
            : row.DetailText.Length > 0 ? row.DetailText : BuildDetails(row.Entry, [], []))); Clipboard.SetContent(data); } catch (Exception ex) { _notice?.Invoke(L.T("复制失败"), ex.Message, InfoBarSeverity.Warning); } };
        menu.Items.Add(copy);
        if (args.TryGetPosition(anchor, out var position)) menu.ShowAt(anchor, new FlyoutShowOptions { Position = position }); else menu.ShowAt(anchor);
    }

    private void ContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_realized.Remove(args.ItemContainer, out var previous) && (!_active || !_rows.Contains(previous)))
        { previous.Icon = null; previous.NotifyIcon(); }
        if (!args.InRecycleQueue && args.Item is AutorunRow row) { _realized[args.ItemContainer] = row; LoadIcon(row); }
        // Preserve images through collection moves without retaining every scrolled row.
        var visible = _realized.Values.ToHashSet();
        foreach (var old in _rows.Where(item => item.Icon is not null && !visible.Contains(item)).Skip(96))
        { old.Icon = null; old.NotifyIcon(); }
    }
    private void LoadRealizedIcons() { foreach (var row in _realized.Values.Distinct()) LoadIcon(row); }
    private async void LoadIcon(AutorunRow row)
    {
        if (_closed || !_active || _icons is null || row.Icon is not null || !_loadingIcons.Add(row)) return;
        try
        {
            var icon = await _icons.GetAsync(row.IconPath);
            if (!_closed && _active && _realized.Values.Contains(row)) { row.Icon = icon; row.NotifyIcon(); }
        }
        catch (OperationCanceledException) { }
        catch (IconRequestDeferredException) { }
        catch { /* Icons never affect startup configuration. */ }
        finally { _loadingIcons.Remove(row); }
    }

    private void ToolbarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ScanActions is null) return;
        var compact = args.NewSize.Width < 480;
        ActionColumn.Width = compact ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumnSpan(Search, compact ? 2 : 1);
        Grid.SetRow(ScanActions, compact ? 1 : 0); Grid.SetColumn(ScanActions, compact ? 0 : 1);
        Grid.SetColumnSpan(ScanActions, compact ? 2 : 1);
        ScanActions.HorizontalAlignment = HorizontalAlignment.Right;
    }
    private void FilterBarSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (StateFilter is null || ComplexMode is null) return;
        var compact = args.NewSize.Width < 380;
        var wrapped = args.NewSize.Width < 740;
        StateColumn.Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ModeColumn.Width = wrapped ? new GridLength(0) : GridLength.Auto;
        Grid.SetColumnSpan(SourceFilter, compact ? 3 : 1);
        Grid.SetRow(StateFilter, compact ? 1 : 0); Grid.SetColumn(StateFilter, compact ? 0 : 1);
        Grid.SetColumnSpan(StateFilter, compact ? 3 : wrapped ? 2 : 1);
        StateFilter.Margin = new Thickness(0, compact ? 8 : 0, 0, 0);
        Grid.SetRow(ComplexMode, compact ? 2 : wrapped ? 1 : 0);
        Grid.SetColumn(ComplexMode, wrapped ? 0 : 2);
        Grid.SetColumnSpan(ComplexMode, wrapped ? 3 : 1);
        ComplexMode.Margin = new Thickness(0, wrapped ? 8 : 0, 0, 0);
    }
}
