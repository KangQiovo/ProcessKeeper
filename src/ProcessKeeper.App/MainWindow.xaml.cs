using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProcessKeeper.Core;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace ProcessKeeper.App;

public sealed partial class MainWindow : Window
{
    private readonly ProcessCollector _collector = new();
    private readonly WhitelistStore _store = new();
    private readonly ProtectionPolicy _policy;
    private readonly ProcessCloser _closer = new();
    private readonly ProcessIconService _icons = new();
    private readonly HashSet<string> _expandedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<AppRow> _appRows = [];
    private readonly ObservableCollection<RuleRow> _ruleRows = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _successNoticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTimeOffset _successNoticeExpiresAt;
    private readonly Dictionary<TreeViewNode, ProcessRecord> _treeProcesses = [];
    private readonly string _historyPath;
    private readonly ActivityLogBuffer _activityLog = new();
    private ProcessSnapshot _snapshot = ProcessSnapshot.Empty;
    private IReadOnlyList<WhitelistRule> _rules = [];
    private bool _ready, _refreshing, _rendering, _dialogOpen, _working, _closed;
    private bool _configurationHealthy = true;
    private string? _selectedKey;
    private string? _selectedRowKey;
    private int? _selectedProcessId;
    private string _lastDetailSignature = "";
    private bool _appRenderQueued;
    private CancellationTokenSource? _closeCancellation;
    private long _treeGeneration;

    public MainWindow() : this(null) { }

    internal MainWindow(string? initialLanguagePreference, Task<byte[]?>? authorAvatarTask = null)
    {
        InitializeComponent();
        AuthorAvatar.Start(authorAvatarTask ?? Task.FromResult<byte[]?>(null));
        Closed += (_, _) => AuthorAvatar.Dispose();
        InitializeRiskMode();
        CompatibilityNotice.IsOpen = CompatibilityNoticeState.ShouldShow();
        ReferencedProjectsList.ItemsSource = ReferencedProjects.All;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProcessKeeper.ico"));
        InitializeAppearance();
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1920, workArea.Width - 80), Math.Min(1320, workArea.Height - 80)));
        WindowSizePolicy.Attach(this);
        var current = Process.GetCurrentProcess();
        _policy = new ProtectionPolicy(current.SessionId, WindowsIdentity.GetCurrent().User?.Value ?? "", current.Id);
        AppsList.ItemsSource = _appRows;
        RulesList.ItemsSource = _ruleRows;
        _historyPath = Path.Combine(Path.GetDirectoryName(_store.FilePath)!, "activity.log");
        try
        {
            _rules = _store.Load();
        }
        catch (Exception ex)
        {
            _configurationHealthy = false;
            ShowNotice(L.T("白名单配置无法读取"), L.T("关闭功能暂时停用。可在白名单页恢复默认规则；原配置会备份。") + ex.Message, InfoBarSeverity.Error);
        }
        try
        {
            if (File.Exists(_historyPath)) _activityLog.LoadFile(_historyPath);
            UpdateHistoryText();
        }
        catch (Exception ex)
        {
            if (_configurationHealthy) ShowNotice(L.T("日志无法读取"), ex.Message, InfoBarSeverity.Warning);
        }
        ConfigLocation.Text = L.T("配置保存在本机：") + _store.FilePath;
        InitializeViewPreferences(initialLanguagePreference);
        _ready = true;
        InitializeInstalledPage();
        InitializeAutorunsPage();
        InitializePresentation();
        InitializeSearch();
        InitializeRowInteractions();
        InitializeUpdates();
        InitializeUninstallPage();
        InitializePerformance();
        InitializeMisc();
        InitializeWhitelistScope();
        InitializeClosePersistence();
        UpdateRules();
        _timer.Tick += async (_, _) => { if (LiveToggle.IsOn) await RefreshAsync(); QueueVisibleIcons(); };
        _successNoticeTimer.Tick += (_, _) =>
        {
            if (_closed || !Notice.IsOpen || Notice.Severity != InfoBarSeverity.Success) { _successNoticeTimer.Stop(); return; }
            if (DateTimeOffset.UtcNow < _successNoticeExpiresAt) return;
            _successNoticeTimer.Stop();
            Notice.IsOpen = false;
        };
        Closed += (_, _) => { _closed = true; _memoryView?.Dispose(); _downloadView?.Dispose(); _performanceView?.Dispose(); _uninstallView?.Dispose(); _autorunsView?.Close(); _timer.Stop(); _successNoticeTimer.Stop(); _avdCancellation?.Cancel(); _closeCancellation?.Cancel(); CloseRecoveryWindows(); _icons.Dispose(); _visibleIcons.Clear(); _realizedContainers.Clear(); };
        Root.Loaded += async (_, _) => { StartInstalledPreload(); _autorunsView?.StartPreload(); _ = _uninstallView?.ScanAsync(); await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) => CancelInstalledWork();
    }

    private async Task RefreshAsync()
    {
        if (!_ready || _refreshing || _dialogOpen || _working || _closed) return;
        _refreshing = true;
        // Periodic sampling keeps the current list visible; show progress only before the first snapshot.
        var showProgress = _snapshot.CapturedAt == DateTimeOffset.MinValue;
        if (showProgress) LoadingRing.IsActive = true;
        try
        {
            var snapshot = await Task.Run(_collector.Capture);
            if (_closed) return;
            _snapshot = snapshot;
            InvalidateSelectionTrees();
            RequestPresentationCapture();
            _autorunsView?.UpdateProcesses(snapshot);
            if (AppsPage.Visibility == Visibility.Visible) RenderApps();
            RefreshInstalledRunningState();
            RefreshStatus.Text = L.F($"更新于 {snapshot.CapturedAt.LocalDateTime:HH:mm:ss}  |  本机只读采集");
            if (RulesPage.Visibility == Visibility.Visible) UpdateRules();
        }
        catch (Exception ex) { ShowNotice(L.T("读取失败"), ex.Message, InfoBarSeverity.Warning); }
        finally { _refreshing = false; if (!_closed && showProgress) LoadingRing.IsActive = false; }
    }

    private ProtectionDecision Decision(ProcessRecord process) => _policy.Evaluate(process, _snapshot, RunningRules);
    private bool DirectlyWhitelisted(ApplicationGroup app)
    {
        var identities = ApplicationPresentationGroups.GetRunningRules(app);
        return identities.Count > 0 && identities.All(identity => _rules.Any(rule => rule.Enabled && rule.Kind == identity.Kind && rule.Value.Equals(identity.Value, StringComparison.OrdinalIgnoreCase)));
    }
    private ApplicationGroup? SelectedApp
    {
        get
        {
            var app = _snapshot.Applications.FirstOrDefault(a => a.Key == _selectedKey);
            if (app is null) return null;
            var visible = DisplayedApplication(app);
            return visible.Processes.Count > 0 ? visible : null;
        }
    }
    private ApplicationGroup DisplayedApplication(ApplicationGroup app) => ShowSystemToggle.IsChecked == true ? app :
        app with { Processes = app.Processes.Where(p => p.Category != RunCategory.System).ToArray() };
    private static string CategoryLabel(RunCategory category) => category switch
    {
        RunCategory.Visible => L.T("有可见窗口"), RunCategory.Minimized => L.T("已最小化"),
        RunCategory.Background => L.T("隐藏后台"), _ => L.T("服务与系统")
    };
    private static string Memory(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0} MB";

    private void RenderApps()
    {
        NativeSelectionTree.For(AppsList)?.Invalidate(AppsPage.Visibility == Visibility.Visible);
        if (!_ready || _closed || AppsPage.Visibility != Visibility.Visible) return;
        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<ApplicationGroup> source = _snapshot.Applications.Select(DisplayedApplication).Where(a => a.Processes.Count > 0);
        if (_hideMicrosoftApps) source = source.Where(a => !_displayCatalog.IsMicrosoft(a));
        if (CategoryFilter.SelectedIndex > 0) source = source.Where(a => (int)a.Category == CategoryFilter.SelectedIndex - 1);
        if (query.Length > 0) source = source.Where(a => ApplicationSearch.MatchesApplication(a, query) || MatchesPlatformQuery(query, _displayCatalog.FindGame(a), _displayCatalog.FindGamePlatform(a)));
        var sorted = SortFilter.SelectedIndex switch
        {
            1 => source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => source.OrderByDescending(a => a.MemoryBytes).ThenBy(a => a.Name),
            3 => source.OrderByDescending(a => a.Processes.Count).ThenBy(a => a.Name),
            _ => source.OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var apps = sorted.ToArray();
        var entries = new List<(ApplicationGroup App, ProcessRecord? Process, string RowKey)>();
        var expandedForSearch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
        {
            entries.Add((app, null, app.Key));
            var matches = query.Length > 0 ? app.Processes.Where(p => ApplicationSearch.MatchesProcess(p, query)).ToArray() : [];
            if (matches.Length > 0 && !_searchCollapsedApps.Contains(app.Key)) expandedForSearch.Add(app.Key);
            if (_expandedApps.Contains(app.Key))
                foreach (var process in OrderedProcesses(app))
                    entries.Add((app, process, $"{app.Key}|{process.Id}:{process.StartTimeUtcTicks}"));
            else if (expandedForSearch.Contains(app.Key))
                foreach (var process in OrderedProcesses(app with { Processes = matches }))
                    entries.Add((app, process, $"{app.Key}|{process.Id}:{process.StartTimeUtcTicks}"));
        }
        entries = GroupRunningRows(entries, query);
        var desired = entries.Select(a => a.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rendering = true;
        try
        {
            for (var i = _appRows.Count - 1; i >= 0; i--) if (!desired.Contains(_appRows[i].RowKey)) _appRows.RemoveAt(i);
            var existing = _appRows.ToDictionary(row => row.RowKey, StringComparer.OrdinalIgnoreCase);
            var processesById = _snapshot.Processes.GroupBy(process => process.Id).ToDictionary(group => group.Key, group => group.First());
            for (var i = 0; i < entries.Count; i++)
            {
                var (app, process, rowKey) = entries[i];
                existing.TryGetValue(rowKey, out var row);
                if (row is null) { row = new AppRow { Key = app.Key, RowKey = rowKey, ProcessId = process?.Id }; _appRows.Insert(i, row); }
                else if (!ReferenceEquals(_appRows[i], row)) NativeListSelection.Move(AppsList, _appRows, _appRows.IndexOf(row), i, item => item.RowKey);
                row.IsPresentationGroup = rowKey.StartsWith(PlatformRowPrefix, StringComparison.Ordinal);
                var platform = _groupGamePlatforms && !row.IsPresentationGroup ? _displayCatalog.FindGamePlatform(app) : null;
                row.PresentationPlatformId = row.IsPresentationGroup ? rowKey.Substring(PlatformRowPrefix.Length) : platform?.Id ?? "";
                row.PresentationDepth = platform is null ? 0 : 1;
                if (row.IsPresentationGroup)
                {
                    row.Name = app.Name; row.IsExpanded = query.Length > 0 || !_collapsedRunningPlatforms.Contains(row.PresentationPlatformId);
                    row.Summary = L.T("游戏平台"); row.ProtectionText = ""; row.CanWhitelist = false;
                    row.Tooltip = app.Description; row.IconPath = _displayCatalog.Clients.FirstOrDefault(c => c.Platform.Id == row.PresentationPlatformId)?.ExecutablePath ?? ""; row.Notify(); continue;
                }
                if (process is null)
                {
                    row.Name = _displayCatalog.FindGame(app)?.Name is { Length: > 0 } gameName ? gameName : app.Name;
                    row.IsExpanded = _expandedApps.Contains(app.Key) || expandedForSearch.Contains(app.Key);
                    row.Summary = L.F($"{CategoryLabel(app.Category)}{(app.IsForeground ? L.T(" | 当前焦点") : "")}  |  {app.Processes.Count} 个进程  |  {Memory(app.MemoryBytes)}");
                    row.IsWhitelisted = DirectlyWhitelisted(app);
                    row.CanWhitelist = _configurationHealthy && !_working && !app.Key.StartsWith("unknown:", StringComparison.Ordinal);
                    var decisions = app.Processes.Select(Decision).ToArray();
                    var protectedCount = decisions.Count(d => d.Protected);
                    row.ProtectionText = decisions.Any(d => d.RuleId is not null) ? L.T("白名单保护") :
                        protectedCount == app.Processes.Count ? L.T("系统 / 身份保护") : protectedCount > 0 ? L.F($"{protectedCount} 个进程受保护") : L.T("可在确认后关闭");
                    row.Tooltip = app.Name + "\n" + app.Description + "\n" + L.T("使用箭头展开或收起进程。");
                }
                else
                {
                    row.Name = $"{process.Name}  |  PID {process.Id}";
                    row.Summary = L.F($"所属软件：{app.Name}") + $" | {CategoryLabel(process.Category)} | {Memory(process.MemoryBytes)} | {process.RoleDescription}";
                    var parent = processesById.GetValueOrDefault(process.ParentId);
                    row.ProtectionText = parent is not null && IsValidParent(process, parent)
                        ? L.F($"由 {parent.Name}（{parent.Id}）启动") : L.T("启动来源：父进程已退出或无法核实");
                    row.Tooltip = $"{process.Name} | PID {process.Id}\n{process.RoleDescription}\n{process.Path}\n{Decision(process).Reason}";
                }
                var iconPath = process?.Path ?? _displayCatalog.ResolveIconPath(app);
                var recovery = process is null ? null : WindowRecoveryHints.ForProcess(process, _snapshot);
                row.RecoveryText = recovery?.Text ?? "";
                row.RecoveryTooltip = recovery?.Tooltip ?? "";
                row.IconPath = iconPath;
                row.Notify();
            }
            var selectedRow = _appRows.FirstOrDefault(r => r.RowKey == _selectedRowKey) ?? _appRows.FirstOrDefault(r => r.RowKey == _selectedKey);
            // Observable row identities preserve every native selected item; updating inspector focus must not replace that set.
            _selectedKey = selectedRow?.Key;
            _selectedRowKey = selectedRow?.RowKey;
            if (selectedRow is null) _selectedProcessId = null;
            EmptyListText.Visibility = apps.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            var hiddenCount = ShowSystemToggle.IsChecked == true ? 0 : _snapshot.Processes.Count(p => p.Category == RunCategory.System);
            SummaryText.Text = L.F($"{apps.Length} 个程序 | {apps.Sum(a => a.Processes.Count)} 个进程 | {_rules.Count(r => r.Enabled)} 条保留规则") + (hiddenCount > 0 ? L.F($" | {hiddenCount} 个系统 / 服务进程已隐藏") : "");
            CloseOthersButton.IsEnabled = _configurationHealthy && !_working && (_snapshot.Processes.Any(p => !Decision(p).Protected) || GetDockPlan(_snapshot.Processes) is not null);
            UpdateDetail();
        }
        finally { _rendering = false; QueueVisibleIcons(); RefreshSelectionButtons(); }
    }

    private IEnumerable<ProcessRecord> OrderedProcesses(ApplicationGroup app) => SortFilter.SelectedIndex switch
    {
        1 => app.Processes.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id),
        2 => app.Processes.OrderByDescending(p => p.MemoryBytes).ThenBy(p => p.Id),
        _ => app.Processes.OrderBy(p => p.Category).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id)
    };

    private void SystemVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        SystemCategoryOption.IsEnabled = ShowSystemToggle.IsChecked == true;
        if (!SystemCategoryOption.IsEnabled && CategoryFilter.SelectedIndex == 4) CategoryFilter.SelectedIndex = 0;
        _lastDetailSignature = "";
        RenderApps();
        if (RulesPage.Visibility == Visibility.Visible) UpdateRules();
        RefreshInstalledRunningState();
        SaveViewPreferences();
    }


    private void AppChevronClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is AppRow row) ToggleAppRow(row);
    }
    private void ToggleAppRow(AppRow row)
    {
        if (_working || row.IsProcess) return;
        if (row.IsPresentationGroup)
        { if (!_collapsedRunningPlatforms.Add(row.PresentationPlatformId)) _collapsedRunningPlatforms.Remove(row.PresentationPlatformId); }
        else if (row.ProcessId is null)
        {
            if (row.IsExpanded) { _expandedApps.Remove(row.Key); if (!string.IsNullOrWhiteSpace(SearchBox.Text)) _searchCollapsedApps.Add(row.Key); }
            else { _searchCollapsedApps.Remove(row.Key); _expandedApps.Add(row.Key); }
        }
        if (_appRenderQueued) return;
        _appRenderQueued = DispatcherQueue.TryEnqueue(() => { _appRenderQueued = false; if (!_closed) RenderApps(); });
    }
    private void UpdateDetail(bool force = false)
    {
        if (_closed || AppsPage.Visibility != Visibility.Visible) return;
        var app = SelectedApp;
        if (app is null)
        {
            DetailName.Text = L.T("选择一个程序");
            DetailSummary.Text = L.T("单击左侧程序展开进程，再选中进程查看详情。");
            DetailProtection.Text = "";
            DetailEvidence.Text = "";
            DetachTreeSelection();
            ProcessTree.RootNodes.Clear();
            _treeProcesses.Clear();
            _lastDetailSignature = "";
            ProcessFacts.Text = "";
            RelationshipText.Text = "";
            AddSelectedButton.IsEnabled = FolderButton.IsEnabled = CloseSelectedButton.IsEnabled = ShowWindowButton.IsEnabled = MoreButton.IsEnabled = RevealHiddenWindowItem.IsEnabled = MinimizeWindowItem.IsEnabled = RestartAvdItem.IsEnabled = false;
            RestartAvdItem.Visibility = Visibility.Collapsed;
            SpecialWindowItem.Visibility = Visibility.Collapsed;
            return;
        }
        DetailName.Text = app.Name;
        var hidden = _snapshot.Applications.FirstOrDefault(a => a.Key == app.Key)?.Processes.Count(p => p.Category == RunCategory.System) ?? 0;
        DetailSummary.Text = L.F($"{CategoryLabel(app.Category)} | {app.Processes.Count} 个进程 | {Memory(app.MemoryBytes)}") + (ShowSystemToggle.IsChecked != true && hidden > 0 ? L.F($"（另 {hidden} 个系统 / 服务已隐藏）") : "") + L.F($"\n文件声明公司：{(string.IsNullOrWhiteSpace(app.Company) ? L.T("未知") : app.Company)}");
        var decisions = app.Processes.Select(Decision).ToArray();
        DetailProtection.Text = string.Join("；", decisions.Select(d => d.Protected ? d.Reason : L.T("未加入白名单，可在确认后关闭")).Distinct().Take(3));
        DetailEvidence.Text = app.IdentityEvidence;
        ToolTipService.SetToolTip(DetailEvidence, app.IdentityEvidence);
        ToolTipService.SetToolTip(DetailProtection, DetailProtection.Text);
        AddSelectedButton.IsEnabled = _configurationHealthy && !_working && !app.Key.StartsWith("unknown:", StringComparison.Ordinal);
        AddSelectedButton.Content = DirectlyWhitelisted(app) ? L.T("移出程序白名单") : L.T("保留此程序");
        FolderButton.IsEnabled = app.Processes.Any(p => !string.IsNullOrWhiteSpace(p.Path));
        ShowWindowButton.IsEnabled = !_working;
        var visibleWindows = WindowCandidates(app).Length;
        var hiddenWindows = WindowCandidates(app, hiddenOnly: true).Length;
        ShowWindowButton.Content = visibleWindows > 0 ? L.T("显示窗口") : hiddenWindows > 0 ? L.T("显示隐藏窗口…") : app.Key == "known:avd" ? L.T("以原生窗口重启…") : HasSpecialEntry(app) ? L.T("打开对应页面…") : L.T("窗口说明");
        RevealHiddenWindowItem.IsEnabled = !_working && hiddenWindows > 0;
        MinimizeWindowItem.IsEnabled = !_working && visibleWindows > 0;
        RestartAvdItem.Visibility = app.Key == "known:avd" ? Visibility.Visible : Visibility.Collapsed;
        RestartAvdItem.IsEnabled = !_working && app.Key == "known:avd";
        SpecialWindowItem.Visibility = HasSpecialEntry(app) ? Visibility.Visible : Visibility.Collapsed;
        SpecialWindowItem.IsEnabled = !_working && HasSpecialEntry(app);
        MoreButton.IsEnabled = !_working;
        CloseSelectedButton.IsEnabled = _configurationHealthy && !_working && (app.Processes.Any(p => !Decision(p).Protected) || GetDockPlan(app.Processes) is not null);
        var relatedIds = app.Processes.Select(p => p.Id).ToHashSet();
        var parentIds = app.Processes.Select(p => p.ParentId).ToHashSet();
        var visibleMembers = string.Join(',', app.Processes.Select(p => $"{p.Id}:{p.StartTimeUtcTicks}"));
        var signature = $"{app.Key}:{ShowSystemToggle.IsChecked}:{visibleMembers}:" + string.Join(',', _snapshot.Processes.Where(p => relatedIds.Contains(p.Id) || relatedIds.Contains(p.ParentId) || parentIds.Contains(p.Id)).Select(p => $"{p.Id}:{p.StartTimeUtcTicks}:{p.ParentId}"));
        foreach (var node in _treeProcesses.Keys.ToArray())
            if (app.Processes.FirstOrDefault(p => p.Id == _treeProcesses[node].Id) is { } latestProcess) _treeProcesses[node] = latestProcess;
        if (!force && signature == _lastDetailSignature)
        {
            if (app.Processes.FirstOrDefault(p => p.Id == _selectedProcessId) is { } selected && ProcessFacts.FocusState == FocusState.Unfocused) ShowProcess(selected);
            return;
        }
        _lastDetailSignature = signature;
        var expandedNodes = _treeProcesses.ToDictionary(pair => (pair.Value.Id, pair.Value.StartTimeUtcTicks), pair => pair.Key.IsExpanded);
        DetachTreeSelection();
        ProcessTree.RootNodes.Clear();
        _treeProcesses.Clear();
        var byId = app.Processes.ToDictionary(p => p.Id);
        var nodes = new Dictionary<int, TreeViewNode>();
        foreach (var process in app.Processes.OrderBy(p => p.Id))
        {
            var node = new TreeViewNode { Content = $"{process.Name}   |   PID {process.Id}", IsExpanded = expandedNodes.GetValueOrDefault((process.Id, process.StartTimeUtcTicks), true) };
            nodes[process.Id] = node;
            _treeProcesses[node] = process;
        }
        foreach (var process in app.Processes.OrderBy(p => p.Id))
        {
            if (byId.TryGetValue(process.ParentId, out var parent) && IsValidParent(process, parent) && !CreatesCycle(process, byId)) nodes[parent.Id].Children.Add(nodes[process.Id]);
            else ProcessTree.RootNodes.Add(nodes[process.Id]);
        }
        if (_selectedProcessId is { } selectedId && nodes.TryGetValue(selectedId, out var selectedNode))
        {
            var generation = _treeGeneration;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_closed && generation == _treeGeneration && _selectedProcessId == selectedId &&
                    AppsPage.Visibility == Visibility.Visible && _treeProcesses.ContainsKey(selectedNode))
                    ProcessTree.SelectedNode = selectedNode;
            });
        }
        if (app.Processes.Count > 0) ShowProcess(app.Processes.FirstOrDefault(p => p.Id == _selectedProcessId) ?? app.Processes.FirstOrDefault(p => p.HasVisibleWindow) ?? app.Processes[0]);
        var relation = new StringBuilder();
        relation.AppendLine(L.T("程序身份")).AppendLine(app.IdentityEvidence).AppendLine();
        relation.AppendLine(L.T("程序与进程的区别")).AppendLine(L.T("一个程序可以包含多个进程。主界面、网页渲染、崩溃报告、后台服务可能分别运行。列表按可核实的程序身份分组；没有足够证据的程序保持独立。")).AppendLine();
        relation.AppendLine(L.T("树状连线的含义")).AppendLine(L.T("连线表示父进程启动了子进程，启动时间先后顺序已核对。它不能单独证明两个进程属于同一软件；树中只展示当前程序内的启动关系。")).AppendLine();
        relation.AppendLine(L.T("关联到其他程序"));
        var relations = new HashSet<string>();
        foreach (var process in app.Processes)
        {
            var parent = _snapshot.Processes.FirstOrDefault(p => p.Id == process.ParentId);
            if (parent is not null && !byId.ContainsKey(parent.Id) && IsValidParent(process, parent)) relations.Add(L.F($"{parent.ApplicationName} / {parent.Name}（{parent.Id}）→ {process.Name}（{process.Id}）：启动来源"));
            foreach (var child in _snapshot.Processes.Where(p => p.ParentId == process.Id && !byId.ContainsKey(p.Id) && IsValidParent(p, process))) relations.Add(L.F($"{process.Name}（{process.Id}）→ {child.ApplicationName} / {child.Name}（{child.Id}）：启动了其他程序"));
        }
        foreach (var text in relations.Take(30)) relation.AppendLine("| " + text);
        if (relations.Count == 0) relation.AppendLine(L.T("当前快照没有可核实的跨程序启动关系。父进程可能已经退出。"));
        relation.AppendLine().AppendLine(L.T("文件版本信息由程序自行声明，不等于验证过的数字签名。未知或无法读取的信息不会补猜。"));
        RelationshipText.Text = relation.ToString();
    }

    private static bool IsValidParent(ProcessRecord child, ProcessRecord parent) => child.Id != parent.Id && parent.StartTimeUtcTicks > 0 && child.StartTimeUtcTicks > 0 && parent.StartTimeUtcTicks <= child.StartTimeUtcTicks;
    private void DetachTreeSelection()
    {
        // WinUI's selected-node vector owns event subscriptions. Release them while
        // the old nodes are still attached; clearing RootNodes first leaves the
        // native selection cleanup touching stale trackers (observed 0xc0000374).
        _treeGeneration++;
        ProcessTree.SelectedNode = null;
    }
    private static bool CreatesCycle(ProcessRecord start, Dictionary<int, ProcessRecord> byId)
    {
        var visited = new HashSet<int> { start.Id };
        var current = start;
        while (byId.TryGetValue(current.ParentId, out var parent)) { if (!visited.Add(parent.Id)) return true; current = parent; }
        return false;
    }

    private void ShowProcess(ProcessRecord process)
    {
        _selectedProcessId = process.Id;
        var parent = _snapshot.Processes.FirstOrDefault(p => p.Id == process.ParentId);
        var parentText = parent is null ? L.F($"{process.ParentId}（已退出或未采集）") : IsValidParent(process, parent) ? L.F($"{parent.Name} | {parent.Id}（核实的启动关系）") : L.F($"{process.ParentId}（父 PID 已复用或时间未知，不能确认关联）");
        var created = process.StartTimeUtcTicks > 0 ? new DateTime(process.StartTimeUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : L.T("无法读取");
        var windows = string.Join("；", process.Windows.Where(w => w.IsTopLevel && !string.IsNullOrWhiteSpace(w.Title))
            .Select(w => $"{WindowStateLabel(w)} | {w.Title}").Distinct());
        var services = string.Join("；", process.Services.Select(s => $"{s.DisplayName} ({s.Name})"));
        ProcessFacts.Text = L.F($"{process.Name}  |  PID {process.Id}\n用途：{process.RoleDescription}\n父进程：{parentText}\n启动于：{created}\n状态：{CategoryLabel(process.Category)}\n内存：{Memory(process.MemoryBytes)}\n产品：{process.ProductName}\n文件说明：{process.Description}\n路径：{(process.Path.Length == 0 ? L.T("权限不足或已退出") : process.Path)}\n账户 SID：{(process.OwnerSid.Length == 0 ? L.T("无法读取") : process.OwnerSid)}\n窗口：{(windows.Length == 0 ? L.T("无可读取标题的顶层窗口") : windows)}\n关联服务：{(services.Length == 0 ? L.T("没有采集到服务映射") : services)}\n保留判定：{Decision(process).Reason}");
    }

    private void UpdateRules() => RenderRules();
    private static string KindLabel(RuleKind kind) => kind switch { RuleKind.Application => L.T("程序身份"), RuleKind.ExecutablePath => L.T("完整文件路径"), RuleKind.ProcessName => L.T("进程名"), _ => L.T("安装目录") };

    private void CommitRules(IReadOnlyList<WhitelistRule> next, string log)
    {
        if (_working) return;
        if (!_configurationHealthy) { ShowNotice(L.T("需要先恢复配置"), L.T("请在白名单页恢复默认规则，再进行修改。"), InfoBarSeverity.Warning); return; }
        try { _store.Save(next); _rules = next; NotifyProfilesRulesChanged(); _lastDetailSignature = ""; UpdateRules(); RenderApps(); Log(log); }
        catch (Exception ex) { ShowNotice(L.T("保存失败"), ex.Message, InfoBarSeverity.Error); RenderApps(); }
    }
    private void ToggleApp(ApplicationGroup app, bool enabled)
    {
        var identities = ApplicationPresentationGroups.GetRunningRules(app);
        var keys = identities.Select(rule => rule.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = _rules.Where(r => !(r.Kind == RuleKind.Application && keys.Contains(r.Value))).ToList();
        if (enabled) next.AddRange(identities);
        CommitRules(next, L.F($"{(enabled ? L.T("加入") : L.T("移出"))}程序白名单：{app.Name}"));
    }
    private void WhitelistChecked(object sender, RoutedEventArgs e)
    {
        var clicked = (sender as FrameworkElement)?.DataContext as AppRow;
        try
        {
            if (_rendering || !_ready || _working || sender is not CheckBox box || box.Tag is not string key) return;
            var app = _snapshot.Applications.FirstOrDefault(a => a.Key == key);
            if (app is null || clicked?.CanWhitelist != true) return;
            var requested = box.IsChecked == true;
            if (requested != DirectlyWhitelisted(app)) ToggleApp(app, requested);
            if (DirectlyWhitelisted(app) == requested) NativeSelectionTree.SetFromAction(box, requested);
        }
        finally
        {
            clicked?.NotifyWhitelistState();
            if (sender is CheckBox checkbox) RestoreActionBinding(checkbox, nameof(AppRow.IsWhitelisted));
        }
    }
    private void KeepSelected(object sender, RoutedEventArgs e) { if (SelectedApp is { } app) ToggleApp(app, !DirectlyWhitelisted(app)); }
    private void RuleToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _rendering || _working || sender is not CheckBox toggle || toggle.Tag is not string id) return;
        var rule = _rules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        var requested = toggle.IsChecked == true;
        if (rule.Enabled != requested) CommitRules(_rules.Select(r => r.Id == id ? r with { Enabled = requested } : r).ToArray(), L.F($"{(requested ? L.T("启用") : L.T("停用"))}规则：{WhitelistStore.GetDisplayName(rule)}"));
        if (_rules.FirstOrDefault(r => r.Id == id)?.Enabled == requested) NativeSelectionTree.SetFromAction(toggle, requested);
        else RestoreActionBinding(toggle, nameof(RuleRow.Enabled));
    }
    private void RemoveRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && _rules.FirstOrDefault(r => r.Id == id) is { } rule) CommitRules(_rules.Where(r => r.Id != id).ToArray(), L.T("移除规则：") + rule.Name);
    }
    private async void AddRule(object sender, RoutedEventArgs e) => await EditRuleDialog(null);
    private async void EditRule(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id }) await EditRuleDialog(_rules.FirstOrDefault(r => r.Id == id));
    }

    private async Task EditRuleDialog(WhitelistRule? existing)
    {
        if (_dialogOpen || _working || !_configurationHealthy) return;
        var name = new TextBox { Header = L.T("规则名称"), PlaceholderText = L.T("例如：我的编辑器"), Text = existing?.Name ?? "" };
        var kind = new ComboBox { Header = L.T("匹配方式"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { L.T("程序身份（从程序列表添加）"), L.T("完整可执行文件路径"), L.T("进程名，例如 editor.exe"), L.T("安装目录（保留整个目录内程序）") }, SelectedIndex = existing is null ? 1 : (int)existing.Kind };
        var value = new TextBox { Header = L.T("匹配值"), Text = existing?.Value ?? "", PlaceholderText = "C:\\Program Files\\MyApp\\MyApp.exe" };
        var descendants = new CheckBox { Content = L.T("同时保留由它启动的子进程"), IsChecked = existing?.IncludeDescendants ?? false };
        var help = new TextBlock { Text = L.T("进程名规则会匹配所有同名进程。目录规则覆盖该目录及其子目录。包含子进程会扩大保留范围。"), TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
        var browse = new Button { Content = L.T("选择可执行文件…") };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file is not null) { kind.SelectedIndex = 1; value.Text = file.Path; if (string.IsNullOrWhiteSpace(name.Text)) name.Text = Path.GetFileNameWithoutExtension(file.Path); }
            }
            catch (Exception ex) { help.Text = L.T("文件选择器不可用，可手动填写完整路径：") + ex.Message; }
        };
        var panel = new StackPanel { Spacing = 14, Width = 450 };
        foreach (var child in new UIElement[] { name, kind, value, browse, descendants, help }) panel.Children.Add(child);
        var dialog = NewDialog(existing is null ? L.T("添加白名单规则") : L.T("编辑白名单规则"), panel, L.T("保存"));
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(value.Text) || kind.SelectedIndex < 0) { args.Cancel = true; help.Text = L.T("请填写规则名称和匹配值。"); }
            else if (kind.SelectedIndex == 0 && existing?.Kind != RuleKind.Application) { args.Cancel = true; help.Text = L.T("程序身份请在运行程序列表中勾选添加，或选择另外一种匹配方式。"); }
        };
        if (await ShowDialog(dialog) == ContentDialogResult.Primary)
        {
            var rule = new WhitelistRule { Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Name = name.Text.Trim(), Kind = (RuleKind)kind.SelectedIndex, Value = value.Text.Trim(), Enabled = existing?.Enabled ?? true, IncludeDescendants = descendants.IsChecked == true };
            CommitRules(_rules.Where(r => r.Id != rule.Id).Append(rule).ToArray(), L.T("保存规则：") + rule.Name);
        }
    }

    private async void ResetRules(object sender, RoutedEventArgs e)
    {
        if (_working || _dialogOpen) return;
        var dialog = NewDialog(L.T("恢复默认白名单？"), L.T("将清空全部白名单配置，恢复为一套空配置。原始文件会另存为备份，所有自定义规则将不再生效。"), L.T("恢复默认"));
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var reset = new WhitelistProfilesStore(Path.GetDirectoryName(_store.FilePath)!).ResetToDefaults();
            _rules = reset.Snapshot.ActiveRules;
            ProfilesHost.Content = null;
            EnsureProfilesView();
            _configurationHealthy = _scopeReadable;
            if (_scopeReadable) Notice.IsOpen = false;
            RefreshWhitelistActionGuards(); UpdateRules(); RenderApps(); Log(L.T("已恢复默认白名单，原配置已备份。"));
        }
        catch (Exception ex) { ShowNotice(L.T("恢复失败"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void ExportRules(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _working || !_configurationHealthy) return;
        var shareRules = RulePortability.ForSharing(_rules);
        var mode = new ComboBox { Header = L.T("导出用途"), ItemsSource = new[] { L.T("分享给其他电脑（仅通用程序身份）"), L.T("完整备份（包含本机路径与名称规则）") }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new StackPanel { Spacing = 14, Width = 490 };
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = L.F($"分享模式可导出 {shareRules.Count} 条通用规则，省略 {_rules.Count - shareRules.Count} 条依赖本机路径、进程名称或未知身份的规则。对方没有或未运行的软件只会显示未匹配。\n\n完整备份包含本机盘符和目录；导入其他电脑时，需要先核对才能启用这些规则。"), TextWrapping = TextWrapping.Wrap });
        if (await ShowDialog(NewDialog(L.T("导出白名单"), panel, L.T("选择保存位置"))) != ContentDialogResult.Primary) return;
        var exported = mode.SelectedIndex == 0 ? shareRules : _rules;
        if (exported.Count == 0 && mode.SelectedIndex == 0)
        {
            ShowNotice(L.T("没有可分享的通用规则"), L.T("本机路径和单独进程名不会自动转换为跨电脑规则。可以导出完整备份，由接收方核对后启用。"), InfoBarSeverity.Informational);
            return;
        }
        _dialogOpen = true;
        try
        {
            var picker = new FileSavePicker(AppWindow.Id) { SuggestedFileName = "ProcessKeeper-" + (mode.SelectedIndex == 0 ? L.T("分享规则-") : L.T("完整备份-")) + DateTime.Now.ToString("yyyyMMdd"), DefaultFileExtension = ".json" };
            picker.FileTypeChoices.Add(L.T("白名单规则 JSON"), new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            WhitelistStore.ExportToFile(file.Path, exported);
            Log(L.F($"已导出 {exported.Count} 条{(mode.SelectedIndex == 0 ? L.T("分享规则") : L.T("完整备份规则"))}：{file.Path}"));
            ShowNotice(L.T("规则已导出"), file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice(L.T("导出失败"), ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private async void ImportRules(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _working || !_configurationHealthy) return;
        IReadOnlyList<WhitelistRule>? imported = null;
        string source = "";
        _dialogOpen = true;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            imported = WhitelistStore.ReadImport(file.Path);
            source = Path.GetFileName(file.Path);
        }
        catch (Exception ex) { ShowNotice(L.T("未导入规则"), L.T("现有白名单保持不变。") + ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
        if (imported is null) return;
        var localCount = imported.Count(r => !RulePortability.IsPortable(r));
        var mode = new ComboBox { Header = L.T("导入方式"), ItemsSource = new[] { L.T("合并到现有规则（相同规则保留现有设置）"), L.T("替换全部现有规则") }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var panel = new StackPanel { Spacing = 14, Width = 510 };
        panel.Children.Add(new TextBlock { Text = L.F($"{source}\n已校验 {imported.Count} 条规则；其中 {localCount} 条本机路径、名称或未知身份规则默认停用。没有或未运行的软件只显示未匹配，不会自动安装或启动。"), TextWrapping = TextWrapping.Wrap });
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 210, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var allowLocal = new CheckBox { Content = L.F($"我已核对，按文件设置启用 {localCount} 条本机规则"), IsChecked = false, Visibility = localCount > 0 ? Visibility.Visible : Visibility.Collapsed };
        void UpdatePreview()
        {
            var prepared = RulePortability.PrepareImport(imported, allowLocal.IsChecked == true);
            preview.Text = string.Join("\n\n", prepared.Select(r => $"{WhitelistStore.GetDisplayName(r)} [{(r.Enabled ? L.T("启用") : L.T("停用"))}]\n{(RulePortability.IsPortable(r) ? L.T("通用程序身份") : L.T("本机规则，需核对")) } | {KindLabel(r.Kind)}：{r.Value}{(r.IncludeDescendants ? L.T("\n含子进程") : "")}"));
        }
        allowLocal.Click += (_, _) => UpdatePreview();
        UpdatePreview();
        panel.Children.Add(preview);
        panel.Children.Add(allowLocal);
        panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = L.T("替换会移除未包含在文件中的保留规则。确认前请检查范围。当前配置会自动保留一份 .bak 备份。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (await ShowDialog(NewDialog(L.T("预览导入的白名单"), panel, L.T("确认导入"))) != ContentDialogResult.Primary) return;
        try
        {
            var prepared = RulePortability.PrepareImport(imported, allowLocal.IsChecked == true);
            var next = mode.SelectedIndex == 1 ? prepared : WhitelistStore.MergeRules(_rules, prepared);
            CommitRules(next, L.F($"从 {source} {(mode.SelectedIndex == 1 ? L.T("替换") : L.T("合并"))}白名单，当前 {next.Count} 条规则"));
        }
        catch (Exception ex) { ShowNotice(L.T("未导入规则"), ex.Message, InfoBarSeverity.Error); }
    }

    private async void CloseSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { } app) await ConfirmAndClose(app.Processes, app.Name);
    }
    private async void CloseOthers(object sender, RoutedEventArgs e) => await ConfirmAndClose(_snapshot.Processes, L.T("未保留的程序"));

    private async Task ConfirmAndClose(IReadOnlyList<ProcessRecord> candidates, string label)
    {
        if (_working || _dialogOpen || !_configurationHealthy) return;
        var frozen = candidates.Where(p => !Decision(p).Protected).ToArray();
        var dockPlan = GetDockPlan(candidates);
        if (frozen.Length == 0 && dockPlan is null) { ShowNotice(L.T("没有可关闭的进程"), L.T("当前选择中的进程均已受保护或已经退出。"), InfoBarSeverity.Informational); return; }
        var appCount = frozen.Select(p => p.ApplicationKey).Distinct().Count();
        var panel = new StackPanel { Spacing = 14, Width = 510 };
        panel.Children.Add(new TextBlock { Text = L.F($"将关闭 {appCount} 个程序中的 {frozen.Length} 个进程。请先保存文件。确认清单被冻结，新启动的进程不会追加。"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBox { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 260, TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = string.Join("\n", frozen.Select(p => $"{p.ApplicationName} | {p.Name} | PID {p.Id}")) });
        var force = new CheckBox { Content = L.T("等待 5 秒后强制结束仍未退出的进程"), IsChecked = false };
        panel.Children.Add(force);
        panel.Children.Add(new TextBlock { Text = L.T("强制结束可能丢失未保存的内容。未勾选时，只发送正常关闭请求，后台无窗口进程可能继续运行。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        if (frozen.Any(SteamShutdownService.IsSteam))
            panel.Children.Add(new TextBlock { Text = L.T(RiskConfirmationMode.IsEnabled
                ? "Steam 会先正常退出，最多等待 30 秒。无视风险模式下，勾选强制结束后将跳过再次确认，可能丢失未保存或未同步的数据。"
                : "Steam 会先通过客户端正常退出，最多等待 30 秒。请先在游戏中保存并退出；若客户端仍在运行，需要再次确认才能强制结束。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var dockCheck = new CheckBox { Content = L.T("同时停止 MyDockFinder 的 MyDock 后台服务"), IsChecked = false };
        if (dockPlan is not null) { panel.Children.Add(dockCheck); panel.Children.Add(new TextBlock { Text = L.T("需要管理员权限；只停止此服务，不修改开机设置。"), FontSize = 12, TextWrapping = TextWrapping.Wrap }); }
        var dialog = NewDialog(L.T("确认关闭 | ") + label, panel, L.T("确认关闭"));
        if (await ShowDialog(dialog) != ContentDialogResult.Primary) return;
        _working = true;
        _closeCancellation = new CancellationTokenSource();
        var closeToken = _closeCancellation.Token;
        AppsList.IsEnabled = Navigation.IsEnabled = AddSelectedButton.IsEnabled = false;
        CloseOthersButton.IsEnabled = CloseSelectedButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        try
        {
            var latest = await Task.Run(_collector.Capture);
            var rules = RunningRules.ToArray();
            ProtectionDecision Recheck(ProcessRecord target)
            {
                var current = latest.Processes.FirstOrDefault(p => p.Id == target.Id && p.StartTimeUtcTicks == target.StartTimeUtcTicks);
                return current is null ? new ProtectionDecision(true, L.T("已退出或 PID 已复用")) : _policy.Evaluate(current, latest, rules);
            }
            if (dockCheck.IsChecked == true && dockPlan is not null) Log(await Task.Run(() => MyDockService.Stop(dockPlan)));
            var forceOrdinary = force.IsChecked == true;
            var progress = new Progress<string>(message => ShowNotice(L.T("正在正常退出"), message, InfoBarSeverity.Informational));
            var report = await Task.Run(() => _closer.CloseDetailedAsync(frozen, forceOrdinary, Recheck, latest, closeToken, progress));
            if (_closed || closeToken.IsCancellationRequested) return;
            var results = report.Results.ToList();
            foreach (var result in results) Log($"{result.ProcessName} [{result.ProcessId}]：{result.Outcome}");
            if (report.SteamPendingForce.Count > 0 && (!RiskConfirmationMode.IsEnabled || forceOrdinary))
            {
                var pending = report.SteamPendingForce.ToArray();
                var warning = new StackPanel { Spacing = 14, MaxWidth = 520 };
                warning.Children.Add(new TextBlock { Text = L.T("Steam 尚未完成退出。它可能在等待游戏、下载或同步。Process Keeper 无法确认文件已保存；建议保留运行并在 Steam 内处理提示。"), TextWrapping = TextWrapping.Wrap });
                warning.Children.Add(new TextBox { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 180, Text = string.Join("\n", pending.Select(p => $"{p.Name} | PID {p.Id}")) });
                var acknowledge = new CheckBox { Content = new TextBlock { Text = L.T("我了解强制结束可能丢失尚未保存或同步的数据"), TextWrapping = TextWrapping.Wrap, MaxWidth = 460 }, IsChecked = false };
                warning.Children.Add(acknowledge);
                var confirm = NewDialog(L.T("Steam 仍在运行"), warning, L.T("强制结束清单内进程"));
                confirm.CloseButtonText = L.T("保留运行");
                confirm.IsPrimaryButtonEnabled = false;
                acknowledge.Checked += (_, _) => confirm.IsPrimaryButtonEnabled = true;
                acknowledge.Unchecked += (_, _) => confirm.IsPrimaryButtonEnabled = false;
                if ((RiskConfirmationMode.IsEnabled || await ShowDialog(confirm) == ContentDialogResult.Primary) && !_closed && !closeToken.IsCancellationRequested)
                {
                    latest = await Task.Run(_collector.Capture);
                    if (_closed || closeToken.IsCancellationRequested) return;
                    var forced = await Task.Run(() => _closer.ForceConfirmedAsync(pending, Recheck, closeToken));
                    if (_closed || closeToken.IsCancellationRequested) return;
                    foreach (var result in forced)
                    {
                        Log($"{result.ProcessName} [{result.ProcessId}]：{result.Outcome}");
                        results.RemoveAll(old => old.ProcessId == result.ProcessId);
                        results.Add(result);
                    }
                }
            }
            var succeeded = results.Count(r => r.Success);
            ShowNotice(L.T("本次清单已处理"), L.F($"{succeeded} 项完成，{results.Count - succeeded} 项未关闭或已跳过。详情见操作记录。"), results.All(r => r.Success) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Log(L.T("关闭操作异常：") + ex.Message); ShowNotice(L.T("操作中止"), ex.Message, InfoBarSeverity.Error); }
        finally
        {
            _closeCancellation?.Dispose(); _closeCancellation = null;
            _working = false;
            if (!_closed)
            {
                LoadingRing.IsActive = false;
                AppsList.IsEnabled = Navigation.IsEnabled = true;
                await RefreshAsync();
            }
        }
    }

    private MyDockServicePlan? GetDockPlan(IReadOnlyList<ProcessRecord> scope)
    {
        if (!scope.Any(p => p.ApplicationKey == "known:mydock")) return null;
        var plan = MyDockService.TryGetPlan();
        if (plan is null) return null;
        var descriptor = new ProcessRecord { Name = "MyDock.exe", Path = plan.ExecutablePath, ApplicationKey = "known:mydock" };
        return ProtectionPolicy.IsDirectlyWhitelisted(descriptor, RunningRules) ? null : plan;
    }

    private ContentDialog NewDialog(string title, object content, string primary) => new()
    {
        XamlRoot = Root.XamlRoot, Title = title, Content = content, PrimaryButtonText = primary,
        CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close, RequestedTheme = Root.ActualTheme
    };
    private async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
    {
        if (_dialogOpen) return ContentDialogResult.None;
        _dialogOpen = true;
        try { return await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        _successNoticeTimer.Stop();
        Notice.Title = title; Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
        {
            _successNoticeExpiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
            _successNoticeTimer.Start();
        }
    }
    private void Log(string message)
    {
        var line = _activityLog.Append(message, DateTimeOffset.Now);
        if (!_closed) UpdateHistoryText();
        try { Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!); File.AppendAllText(_historyPath, line + Environment.NewLine); }
        catch (Exception ex) { ShowNotice(L.T("日志未保存"), ex.Message, InfoBarSeverity.Warning); }
    }
    private async void NavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || args.SelectedItem is not NavigationViewItem item) return;
        var page = item.Tag?.ToString();
        AppsPage.Visibility = page == "apps" ? Visibility.Visible : Visibility.Collapsed;
        RulesPage.Visibility = page == "rules" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = page == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        InstalledPage.Visibility = page == "installed" ? Visibility.Visible : Visibility.Collapsed;
        AutorunsPage.Visibility = page == "autoruns" ? Visibility.Visible : Visibility.Collapsed;
        UninstallPage.Visibility = page == "uninstall" ? Visibility.Visible : Visibility.Collapsed;
        MiscPage.Visibility = page == "misc" ? Visibility.Visible : Visibility.Collapsed;
        _autorunsView?.SetActive(page == "autoruns");
        PageTitle.Text = page == "misc" ? L.T("杂项") : page == "uninstall" ? L.T("应用卸载") : page == "autoruns" ? L.T("自启动") : page == "rules" ? L.T("白名单") : page == "history" ? L.T("操作记录") : page == "appearance" ? L.T("设置") : page == "installed" ? L.T("已安装应用") : L.T("运行中的程序");
        PageSubtitle.Text = page == "misc" ? L.T("按需优化内存、下载文件与查看实时性能。") : page == "uninstall" ? L.T("查看常规与隐藏卸载项，确认后移除不需要的软件。") : page == "autoruns" ? L.T("查看登录、任务和服务等后台启动入口。") : page == "rules" ? L.T("把需要的程序留下，规则由你决定。") : page == "history" ? L.T("每次更改和关闭，即时记录。") : page == "appearance" ? L.T("外观、备份迁移与应用诊断。") : page == "installed" ? L.T("软件未运行，也可以提前加入白名单。") : L.T("看清每个程序，让需要的留下。");
        if (page == "apps") RenderApps();
        if (page == "rules") UpdateRules();
        if (page == "history") QueueHistoryScroll();
        if (page == "installed") await EnsureInstalledLoadedAsync();
        QueueVisibleIcons();
    }
    private void AppSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NativeSelectionTree.For(AppsList)?.IsApplying == true) return;
        RefreshSelectionButtons();
    }
    private void ProcessInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is TreeViewNode node && _treeProcesses.TryGetValue(node, out var process)) ShowProcess(process);
    }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (_ready && !_changingViews) { RenderApps(); SaveViewPreferences(); } }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OpenSelectedFolder(object sender, RoutedEventArgs e)
    {
        var path = SelectedProcess?.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) path = SelectedApp?.Processes.Select(p => p.Path).FirstOrDefault(File.Exists);
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true }); }
        catch (Exception ex) { ShowNotice(L.T("无法打开位置"), ex.Message, InfoBarSeverity.Warning); }
    }

    private ProcessRecord? SelectedProcess => SelectedApp?.Processes.FirstOrDefault(p => p.Id == _selectedProcessId);
    private void CopySelectedInformation(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess is not { } process) return;
        CopyText(L.F($"程序：{process.ApplicationName}\n进程：{process.Name}\nPID：{process.Id}\n父 PID：{process.ParentId}\n用途：{process.RoleDescription}\n状态：{CategoryLabel(process.Category)}\n路径：{process.Path}"), L.T("进程信息已复制"));
    }
    private void CopySelectedPath(object sender, RoutedEventArgs e)
    {
        if (SelectedProcess is { Path.Length: > 0 } process) CopyText(process.Path, L.T("文件路径已复制"));
        else ShowNotice(L.T("路径不可用"), L.T("当前进程的路径无法读取。"), InfoBarSeverity.Informational);
    }
    private void CopyText(string text, string message)
    {
        try
        {
            var data = new DataPackage(); data.SetText(text);
            Clipboard.SetContent(data); Clipboard.Flush();
            ShowNotice(message, "", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice(L.T("复制失败"), ex.Message, InfoBarSeverity.Warning); }
    }
}
