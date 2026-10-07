using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;

public partial class MainWindow : Window
{
    private readonly LegacyBackend _backend;
    private readonly string _directory;
    private readonly CancellationTokenSource _life = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly ObservableCollection<LegacyRow> _rows = new();
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _searchCollapsed = new(StringComparer.Ordinal);
    private readonly LegacyIconCache _icons = new();
    private readonly ActivityLogBuffer _history = new();
    private readonly Dictionary<int, bool> _livePages = new() { [0] = true, [1] = true, [2] = true };
    private ProcessSnapshot _snapshot = ProcessSnapshot.Empty;
    private IReadOnlyList<InstalledApplication> _installed = Array.Empty<InstalledApplication>();
    private AutorunSnapshot _autoruns = new();
    private IReadOnlyList<WhitelistRule> _rules = Array.Empty<WhitelistRule>();
    private ViewPreferences _view = new();
    private AppearancePreferences _appearance = new();
    private bool _ready, _closed, _busy, _capturing, _scanningInstalled, _scanningAutoruns, _rulesReadable = true;
    private bool _collapsed;
    private bool _autorunComplex;
    private int _autorunSource, _autorunState;
    private int _page, _renderVersion;
    private DateTime _lastInstalled = DateTime.MinValue;
    private CancellationTokenSource? _render;
    public MainWindow() : this(new LegacyBackend(), null) { }
    public MainWindow(LegacyBackend backend, string? configurationDirectory, Task<byte[]?>? authorAvatarTask = null)
    {
        _backend = backend;
        _directory = configurationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        InitializeComponent(); FitWorkingArea(_backend.WorkingArea()); List.ItemsSource = _rows; LegacyRowInteraction.Attach(List);
        BeginAuthorAvatar(authorAvatarTask);
        RiskConfirmationMode.Changed += RiskModeChanged;
        List.ContextMenu = new ContextMenu();
        Loaded += async (_, _) => await InitializeAsync();
        Closing += CloseAfterPerformanceSave;
        InitializeWindowMotion();
        Loaded += RevealShell;
        Closed += (_, _) => { _closed = true; _memoryView?.Dispose(); _downloadView?.Dispose(); _performanceView?.Dispose(); _uninstallView?.Dispose(); RiskConfirmationMode.Changed -= RiskModeChanged; _life.Cancel(); _render?.Cancel(); _updateCheckCancellation?.Cancel(); _updatePromptTimer.Stop(); _buildDateTimer.Stop(); _refresh.Stop(); _searchDelay.Stop(); _noticeTimer.Stop(); };
        _refresh.Tick += async (_, _) => await RefreshLiveAsync();
        _searchDelay.Tick += async (_, _) => { _searchDelay.Stop(); await RenderAsync(); };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Status.Text = ""; };
        _updatePromptTimer.Tick += async (_, _) => await ShowPendingUpdateAsync();
        _buildDateTimer.Tick += (_, _) => RefreshBuildDateLabel();
    }
    private void FitWorkingArea(Rect area)
    {
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0 || double.IsInfinity(area.Width) || double.IsInfinity(area.Height)) return;
        var availableWidth = Math.Max(1, area.Width - 16); var availableHeight = Math.Max(1, area.Height - 16);
        MinWidth = Math.Min(900, availableWidth); MinHeight = Math.Min(600, availableHeight);
        Width = Math.Min(Math.Max(Width, MinWidth), availableWidth); Height = Math.Min(Math.Max(Height, MinHeight), availableHeight);
    }
    private async Task InitializeAsync()
    {
        try { _view = new ViewPreferencesStore(_directory).Load(); }
        catch (Exception ex) { Notice(ex.Message); }
        L.Language = LanguageResolver.ResolveCurrent(_view.Language);
        _appearance = new AppearancePreferencesStore(_directory).Load(out var warning);
        try { _updates = new UpdatePreferencesStore(_directory).Load(); }
        catch (Exception ex) { _updatesReadable = false; _updateStatus = L.T("更新设置无法读取") + " | " + ex.Message; }
        try { _rules = new WhitelistStore(_directory).Load(); }
        catch (Exception ex) { _rulesReadable = false; Notice(ex.Message); }
        try { _history.LoadFile(Path.Combine(_directory, "activity.log")); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { Notice(ex.Message); }
        _livePages[0] = _livePages[1] = _livePages[2] = _view.LiveRefresh;
        InitializeWhitelistScope(); ApplyTheme(); ConfigureLanguage();
        ShowSystem.IsChecked = _view.ShowSystemProcesses; HistoryAuto.IsChecked = _view.HistoryAutoScroll;
        _ready = true;
        Navigation.SelectedIndex = 0;
        if (!_backend.IsAdministrator) { ShowPermission(); return; }
        var onboarding = new OnboardingStore(_directory);
        if (!onboarding.IsCompleted(out _)) ShowOnboarding(false);
        _refresh.Start();
        _ = InitializeUpdatesAsync();
        EnsureUninstallPage(); _ = _uninstallView?.ScanAsync();
        try { EnsurePerformance(); EnsureMisc(); } catch (Exception ex) { Notice(ex.Message); }
        await Task.WhenAll(CaptureAsync(), ScanInstalledAsync(), ScanAutorunsAsync());
        if (warning is not null) Notice(warning);
    }
    private void ConfigureLanguage()
    {
        var ready = _ready; _ready = false;
        Navigation.Items.Clear();
        string[] names = { "运行中的程序", "已安装应用", "白名单", "自启动", "操作记录", "设置", "应用卸载", "杂项" };
        string[] symbols = { "▤", "▥", "✓", "◴", "↶", "⚙", "⊗", "◇" };
        int[] order = { 0, 1, 6, 3, 2, 4, 5, 7 };
        foreach (var i in order) Navigation.Items.Add(new ListBoxItem { Tag = i, Content = _collapsed ? symbols[i] : symbols[i] + "   " + L.T(names[i]), ToolTip = L.T(names[i]), Padding = new Thickness(8, 12, 4, 12) });
        Navigation.SelectedIndex = Array.IndexOf(order, _page);
        TestingText.Text = L.T("测试版本不代表最终品质"); TestingText.Visibility = !_collapsed && BuildInfo.IsPreviewBuild ? Visibility.Visible : Visibility.Collapsed;
        LocalText.Text = L.F($"版本：{ProcessKeeper.Core.ReleaseIdentity.DisplayVersion(CurrentAppVersion)}"); LocalText.Margin = new Thickness(0, BuildInfo.IsPreviewBuild ? 7 : 0, 0, 0);
        CompatibilityText.Text = L.T("当前使用兼容运行环境，部分效果和功能可能与新系统不同。");
        CompatibilityNotice.Visibility = CompatibilityNoticeState.ShouldShow(true) ? Visibility.Visible : Visibility.Collapsed;
        Live.Content = L.T("实时更新"); ShowSystem.Content = L.T("显示系统进程"); AutorunMode.OffContent = L.T("简单"); AutorunMode.OnContent = L.T("复杂"); HistoryAuto.Content = L.T("自动滚动");
        RefreshButton.Content = L.T("刷新"); AddButton.Content = L.T("添加"); CloseButton.Content = L.T("关闭未保留的程序");
        ImportButton.Content = L.T("导入"); ExportButton.Content = L.T("导出");
        Search.ToolTip = L.T("搜索软件、进程、PID 或路径");
        UpdateRiskModeUi();
        _ready = ready; ConfigurePage();
        if (_performanceView is not null && _performanceLanguage != L.Language)
        {
            try { EnsurePerformance(); EnsureMisc(); }
            catch (Exception ex) { Notice(ex.Message); }
        }
    }
    private void ConfigurePage()
    {
        if (TitleText is null) return;
        var ready = _ready; _ready = false;
        string[] titles = { "运行中的程序", "已安装应用", "白名单", "自启动", "操作记录", "设置", "应用卸载", "杂项" };
        ConfigurePresentationOptions();
        RefreshSelectionButton();
        SelectAllButton.Visibility = _page < 4 ? Visibility.Visible : Visibility.Collapsed;
        SelectAllButton.ToolTip = L.T("选择当前列表中显示的应用和子项目，不更改白名单。");
        string[] subtitles = { "单击展开或折叠 | 勾选保留或选择", "单击展开或折叠 | 勾选保留或选择 | 推断标签可能有误，请谨慎甄别", "单击展开或折叠 | 勾选启用或选择 | 规则自动保存", "单击展开或折叠 | 勾选启用或选择", "每次更改和关闭，即时记录。", "语言、外观与备份。", "单击展开或折叠 | 勾选选择卸载项目", "按需优化内存、下载文件与查看实时性能。" };
        TitleText.Text = L.T(titles[_page]); SubtitleText.Text = L.T(subtitles[_page]);
        Toolbar.Visibility = Filters.Visibility = _page < 4 ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = _page < 4 ? Visibility.Visible : Visibility.Collapsed;
        SettingsScroll.Visibility = _page == 5 ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = _page == 4 ? Visibility.Visible : Visibility.Collapsed;
        UninstallHost.Visibility = _page == 6 ? Visibility.Visible : Visibility.Collapsed;
        MiscHost.Visibility = _page == 7 ? Visibility.Visible : Visibility.Collapsed;
        WhitelistScopeExpander.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed; WhitelistScopeExpander.Header = L.T("白名单作用范围");
        if (_page == 7) EnsureMisc();
        if (_page == 6) EnsureUninstallPage();
        Live.Visibility = _page < 3 ? Visibility.Visible : Visibility.Collapsed;
        Live.IsChecked = _page < 3 && _livePages[_page];
        ShowSystem.Visibility = _page < 3 ? Visibility.Visible : Visibility.Collapsed;
        AutorunMode.Visibility = _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        AutorunMode.IsChecked = _autorunComplex;
        Sort.Visibility = _page == 0 || _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        Drive.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        AddButton.Visibility = _page == 1 || _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = ExportButton.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;
        Category.Visibility = _page == 0 || _page == 3 ? Visibility.Visible : Visibility.Collapsed;
        Category.ItemsSource = (_page == 3 ? (_autorunComplex ? new[] { "所有来源", "注册表登录项", "启动文件夹", "计划任务", "服务", "驱动", "高级项目", "WMI 订阅", "打包应用启动项" } : new[] { "全部启动项", "常规启动项", "隐藏启动项" }) : (_view.ShowSystemProcesses ? new[] { "全部", "可见窗口", "最小化", "后台运行", "系统进程" } : new[] { "全部", "可见窗口", "最小化", "后台运行" })).Select(L.T).ToArray();
        Category.SelectedIndex = _page == 0 ? _view.CategoryFilter : _page == 3 ? _autorunSource : 0;
        Sort.ItemsSource = (_page == 3 ? new[] { "所有状态", "已启用", "已禁用", "状态未知", "可更改" } : new[] { "默认", "名称", "内存占用", "进程数量" }).Select(L.T).ToArray();
        Sort.SelectedIndex = _page == 0 ? _view.SortOrder : _page == 3 ? _autorunState : 0;
        UpdateDrives(); _ready = ready;
        if (_page == 5 && (SettingsContent.Children.Count == 0 || _settingsLanguage != L.Language)) BuildSettings(); if (_page == 4) UpdateHistory();
    }
    private async void Navigate(object sender, SelectionChangedEventArgs e)
    { if (!_ready || Navigation.SelectedItem is not ListBoxItem { Tag: int page }) return; _page = page; _render?.Cancel(); ConfigurePage(); await RenderAsync(); }
    private void ToggleNavigation(object sender, RoutedEventArgs e)
    { _collapsed = !_collapsed; NavColumn.Width = new GridLength(_collapsed ? 68 : 196); Brand.Visibility = TestingText.Visibility = LocalText.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible; ConfigureLanguage(); }
    private void DismissCompatibility(object sender, RoutedEventArgs e) { CompatibilityNoticeState.Dismissed = true; CompatibilityNotice.Visibility = Visibility.Collapsed; }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!_ready) return; _searchCollapsed.Clear(); _render?.Cancel(); _searchDelay.Stop(); _searchDelay.Start(); }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!_ready) return; if (_page == 0) _view = _view with { SortOrder = Math.Max(0, Sort.SelectedIndex), CategoryFilter = Math.Max(0, Category.SelectedIndex) }; if (_page == 3) { _autorunSource = Math.Max(0, Category.SelectedIndex); _autorunState = Math.Max(0, Sort.SelectedIndex); } if (_page == 1) _view = _view with { InstalledDrive = Drive.SelectedIndex > 0 ? Drive.SelectedItem?.ToString() ?? "" : "" }; SaveView(); await RenderAsync(); }
    private async void SystemChanged(object sender, RoutedEventArgs e) { if (!_ready) return; _view = _view with { ShowSystemProcesses = ShowSystem.IsChecked == true, CategoryFilter = ShowSystem.IsChecked != true && _view.CategoryFilter == 4 ? 0 : _view.CategoryFilter }; SaveView(); ConfigurePage(); await RenderAsync(); }
    private async void AutorunModeChanged(object sender, RoutedEventArgs e) { if (!_ready) return; _autorunComplex = AutorunMode.IsChecked == true; _autorunSource = 0; ConfigurePage(); await RenderAsync(); }
    private void LiveChanged(object sender, RoutedEventArgs e) { if (!_ready || _page >= 3) return; _livePages[0] = _livePages[1] = _livePages[2] = Live.IsChecked == true; _view = _view with { LiveRefresh = Live.IsChecked == true }; SaveView(); }
    private void HistoryAutoChanged(object sender, RoutedEventArgs e) { if (!_ready) return; _view = _view with { HistoryAutoScroll = HistoryAuto.IsChecked == true }; SaveView(); UpdateHistory(); }
    private void SaveView() { try { new ViewPreferencesStore(_directory).Save(_view); } catch (Exception ex) { Notice(ex.Message); } }
    private async Task RefreshLiveAsync()
    {
        if (!_ready || !_view.LiveRefresh || !_backend.IsAdministrator || _busy || _closed) return;
        await CaptureAsync();
        if (_page == 1 && DateTime.UtcNow - _lastInstalled > TimeSpan.FromMinutes(1)) await ScanInstalledAsync();
    }
    private async Task CaptureAsync()
    {
        if (_capturing || _closed) return; _capturing = true;
        try { var result = await Task.Run(_backend.Capture, _life.Token); if (_closed || _life.IsCancellationRequested) return; _snapshot = result; RequestPresentationCapture(); if (_page < 3 && _livePages[_page]) await RenderAsync(); }
        catch (OperationCanceledException) { } catch (Exception ex) { Notice(ex.Message); }
        finally { _capturing = false; }
    }
    private async Task ScanInstalledAsync()
    {
        if (_scanningInstalled || _closed) return; _scanningInstalled = true;
        try { var result = await Task.Run(() => _backend.ScanInstalled(_life.Token), _life.Token); if (_closed || _life.IsCancellationRequested) return; _installed = result; RequestPresentationCapture(); _lastInstalled = DateTime.UtcNow; UpdateDrives(); if (_page == 1 || _page == 2) await RenderAsync(); }
        catch (OperationCanceledException) { } catch (Exception ex) { Notice(ex.Message); }
        finally { _scanningInstalled = false; }
    }
    private async Task ScanAutorunsAsync()
    {
        if (_scanningAutoruns || _closed) return; _scanningAutoruns = true;
        try { var result = await Task.Run(() => _backend.ScanAutoruns(_life.Token), _life.Token); if (_closed || _life.IsCancellationRequested) return; _autoruns = result; RequestPresentationCapture(); if (_page == 3) await RenderAsync(); }
        catch (OperationCanceledException) { } catch (Exception ex) { Notice(ex.Message); }
        finally { _scanningAutoruns = false; }
    }
    private void UpdateDrives()
    {
        if (Drive is null) return; var ready = _ready; _ready = false;
        var items = new[] { L.T("所有盘符") }.Concat(InstalledApplicationSearch.Drives(AllInstalledApplications())).ToArray(); Drive.ItemsSource = items;
        var selected = Array.IndexOf(items, _view.InstalledDrive); Drive.SelectedIndex = selected >= 0 ? selected : 0; _ready = ready;
    }
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (_page == 1) await ScanInstalledAsync(); else if (_page == 3) await ScanAutorunsAsync(); else await CaptureAsync(); await RenderAsync(); }
    private void Notice(string text, bool success = false) { if (_closed) return; Status.Text = text; _noticeTimer.Stop(); if (success) _noticeTimer.Start(); }
    private void Log(string text)
    {
        var line = _history.Append(text, DateTimeOffset.Now);
        try { Directory.CreateDirectory(_directory); File.AppendAllText(Path.Combine(_directory, "activity.log"), line + Environment.NewLine); }
        catch (Exception ex) { Notice(ex.Message); }
        UpdateHistory();
    }
    private void UpdateHistory()
    {
        var offset = HistoryScroll.VerticalOffset; HistoryText.Text = _history.Render();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { if (_closed || _page != 4) return; HistoryScroll.UpdateLayout(); if (_view.HistoryAutoScroll) HistoryScroll.ScrollToEnd(); else HistoryScroll.ScrollToVerticalOffset(offset); }));
    }
}
