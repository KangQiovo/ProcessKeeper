using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public partial class MainWindow
{
    private const int MaximumRenderedRows = 20000;
    private const int MaximumRuleChildren = 512;
    private readonly SemaphoreSlim _rowBuildGate = new(1, 1);
    private sealed class RowLimitReachedException : Exception { }
    private async Task RenderAsync()
    {
        if (!_ready || _closed || _page > 3) return;
        _render?.Cancel(); _render?.Dispose(); _render = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var token = _render.Token; var version = ++_renderVersion; var page = _page;
        var query = Search.Text.Trim(); var category = Category.SelectedIndex; var sort = Sort.SelectedIndex;
        var drive = Drive.SelectedIndex > 0 ? Drive.SelectedItem?.ToString() ?? "" : "";
        var systems = _view.ShowSystemProcesses; var expanded = new HashSet<string>(_expanded);
        var autorunComplex = _autorunComplex;
        var collapsed = new HashSet<string>(_searchCollapsed);
        var snapshot = _snapshot; var installed = AllInstalledApplications();
        var rules = page == 0 ? RunningRules : page == 1 && !_whitelistScope.Installed ? Array.Empty<WhitelistRule>() : _rules;
        var autoruns = _autoruns;
        var display = _displayCatalog; var groupPlatforms = _view.GroupGamePlatforms; var hideMicrosoft = _view.HideMicrosoftApps;
        var collapsedPlatforms = new HashSet<string>(_collapsedPlatforms);
        var uninstallEntries = _uninstallView?.InventoryEntries.ToArray() ?? Array.Empty<UninstallEntry>();
        try
        {
            List<LegacyRow> result;
            await _rowBuildGate.WaitAsync(token);
            try { result = await Task.Run(() => ApplyPresentation(CreateRows(page, query, category, sort, drive, systems, expanded, snapshot, installed, rules, autoruns, token, collapsed, autorunComplex, display, uninstallEntries), page, query, display, groupPlatforms, hideMicrosoft, collapsedPlatforms, collapsed), token); }
            finally { _rowBuildGate.Release(); }
            if (token.IsCancellationRequested || _page != page || version != _renderVersion) return;
            var selected = (List.SelectedItem as LegacyRow)?.Id;
            var desired = new HashSet<string>(result.Select(row => row.Id), StringComparer.Ordinal);
            for (int i = _rows.Count - 1; i >= 0; i--)
                if (!desired.Contains(_rows[i].Id))
                {
                    _restoringTreeSelection = true;
                    try { _rows.RemoveAt(i); } finally { _restoringTreeSelection = false; }
                }
            var existing = _rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            var changedIcons = new List<LegacyRow>();
            for (int i = 0; i < result.Count; i++)
            {
                if (token.IsCancellationRequested || _page != page || version != _renderVersion) return;
                var incoming = result[i];
                if (existing.TryGetValue(incoming.Id, out var row))
                {
                    if (!ReferenceEquals(_rows[i], row))
                    {
                        _restoringTreeSelection = true;
                        try { _rows.Move(_rows.IndexOf(row), i); } finally { _restoringTreeSelection = false; }
                    }
                    if (!row.IconPath.Equals(incoming.IconPath, StringComparison.OrdinalIgnoreCase) ||
                        !row.Path.Equals(incoming.Path, StringComparison.OrdinalIgnoreCase)) changedIcons.Add(row);
                    row.Apply(incoming);
                }
                else { incoming.Notify(); _rows.Insert(i, incoming); }
                if (i % 64 == 63) await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            var selectedRow = _rows.FirstOrDefault(row => row.Id == selected);
            if (List.SelectionMode == SelectionMode.Single && !ReferenceEquals(List.SelectedItem, selectedRow)) List.SelectedItem = selectedRow;
            foreach (var row in _rows.Take(96).Where(row => row.Icon is null)) _ = ApplyIconAsync(row, version, token);
            foreach (var row in changedIcons)
                if (row.Icon is null && List.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem)
                    _ = ApplyIconAsync(row, version, token);
            await RestoreTreeSelectionAsync();
            RefreshSelectionButton();
        }
        catch (OperationCanceledException) { } catch (Exception ex) { Notice(ex.Message); }
    }
    private List<LegacyRow> CreateRows(int page, string query, int category, int sort, string drive, bool systems,
        HashSet<string> expanded, ProcessSnapshot snapshot, IReadOnlyList<InstalledApplication> installed, IReadOnlyList<WhitelistRule> rules, AutorunSnapshot autoruns, CancellationToken token, HashSet<string> collapsed, bool autorunComplex = false, ApplicationDisplayCatalog? display = null,
        IReadOnlyList<UninstallEntry>? uninstallEntries = null)
    {
        token.ThrowIfCancellationRequested();
        display ??= ApplicationDisplayCatalog.Empty;
        var rows = new List<LegacyRow>();
        var executableIdentity = new InstalledExecutableIdentity(uninstallEntries);
        void Add(LegacyRow row)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Count >= MaximumRenderedRows) throw new RowLimitReachedException();
            rows.Add(row);
        }
        bool Open(string id) => query.Length > 0 ? !collapsed.Contains(id) : expanded.Contains(id);
        try
        {
        if (page == 0)
        {
            IEnumerable<ApplicationGroup> apps = snapshot.Applications.Select(app => app with { Processes = app.Processes.Where(p => systems || !p.IsSystem && p.Category != RunCategory.System).ToArray() })
                .Where(app => app.Processes.Count > 0 && (ApplicationSearch.MatchesApplication(app, query) || MatchesPlatformQuery(query, display.FindGame(app), display.FindGamePlatform(app))));
            if (category > 0) apps = apps.Where(app => (int)app.Category == category - 1);
            apps = sort == 2 ? apps.OrderByDescending(app => app.MemoryBytes) : sort == 1 ? apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase) : sort == 3 ? apps.OrderByDescending(app => app.Processes.Count) : apps.OrderBy(app => app.Category).ThenByDescending(app => app.MemoryBytes);
            foreach (var app in apps)
            {
                token.ThrowIfCancellationRequested(); var id = "app:" + app.Key;
                var kept = app.Processes.All(p => _backend.Policy.Evaluate(p, snapshot, rules).Protected);
                Add(new LegacyRow { Id = id, Name = app.Name, Summary = CategoryName(app.Category) + " | " + Memory(app.MemoryBytes) + " | " + app.Processes.Count + " " + L.T("进程"), Path = app.Processes.FirstOrDefault(p => p.Path.Length > 0)?.Path ?? "", IconPath = display.ResolveIconPath(app), Detail = app.Description + "\n" + app.IdentityEvidence, Model = app, Expanded = Open(id), HasAction = true, CanAct = !_busy && _rulesReadable, IsActionChecked = kept, ActionLabel = kept ? L.T("已保留") : L.T("保留"), ApplicationKey = app.Key });
                if (Open(id)) foreach (var process in app.Processes) Add(ProcessRow(process, app.Name, app.Key, snapshot, id));
            }
        }
        else if (page == 1)
        {
            var search = new InstalledApplicationSearch(snapshot.Processes, systems);
            foreach (var app in installed.Where(app => search.Matches(app, query, drive) || search.Matches(app, "", drive) && MatchesPlatformQuery(query, display.FindGame(app), display.FindGamePlatform(app))))
            {
                token.ThrowIfCancellationRequested(); var id = "installed:" + app.Id;
                var kept = InstalledApplicationIsKept(app, rules);
                var mainIdentity = executableIdentity.ResolveMain(app);
                Add(new LegacyRow { Id = id, Name = app.Name, Summary = app.Publisher + " | " + app.Executables.Count + " " + L.T("可执行文件"), Path = mainIdentity.Status == InstalledMainIdentityStatus.Resolved ? mainIdentity.ExecutablePath : app.InstallLocation, IconPath = display.ResolveIconPath(app, executableIdentity), Detail = InstalledApplicationEvidence(app), Model = app, Expanded = Open(id), HasAction = true, CanAct = !_busy && _rulesReadable, IsActionChecked = kept, ActionLabel = kept ? L.T("已保留") : L.T("保留") });
                if (Open(id)) foreach (var executable in executableIdentity.OrderExecutables(app))
                {
                    var component = ExecutableRow(executable, app.Name, id, executableIdentity.Classify(app, executable));
                    if (app.Installations.Count > 1) component.Summary += " | " + Path.GetDirectoryName(executable.Path);
                    var processes = search.ProcessesFor(executable).ToArray();
                    component.HasChildren = processes.Length > 0; component.Expanded = component.HasChildren && Open(component.Id);
                    Add(component);
                    if (component.Expanded) foreach (var process in processes) Add(ProcessRow(process, app.Name, app.ApplicationKey, snapshot, component.Id));
                }
            }
        }
        else if (page == 2)
        {
            var matcher = new RuleProcessMatcher(snapshot); var offline = query.Length == 0 ? new HashSet<string>() : new RuleSearchIndex(installed).MatchingRuleIds(rules, query);
            foreach (var family in ApplicationPresentationGroups.GroupRules(rules, installed, snapshot))
            {
            var start = rows.Count; var grouped = family.Rules.Count > 1;
            foreach (var rule in family.Rules)
            {
                token.ThrowIfCancellationRequested();
                var matches = matcher.Match(rule).Where(match => systems || !match.Process.IsSystem && match.Process.Category != RunCategory.System).ToArray();
                if (!ApplicationSearch.MatchesRule(rule, WhitelistStore.GetDisplayName(rule), matches, query) && !offline.Contains(rule.Id)) continue;
                var id = "rule:" + rule.Id;
                var rulePath = matches.FirstOrDefault()?.Process.Path ?? (rule.Kind == RuleKind.ExecutablePath || rule.Kind == RuleKind.Directory ? rule.Value : "");
                Add(new LegacyRow { Id = id, ParentId = grouped ? family.Key : "", IsChild = grouped, Name = WhitelistStore.GetDisplayName(rule), Summary = (rule.Enabled ? L.T("已启用") : L.T("已禁用")) + " | " + rule.Value, Path = rulePath, IconPath = display.ResolveGameIconPath(rulePath), Detail = rule.Kind + " | " + matches.Length + " " + L.T("进程"), Model = rule, Expanded = Open(id), HasAction = true, CanAct = !_busy && _rulesReadable, IsActionChecked = rule.Enabled, ActionLabel = rule.Enabled ? L.T("禁用此项") : L.T("启用此项") });
                if (Open(id))
                {
                    var children = 0;
                    var paths = new HashSet<string>(matches.Select(match => match.Process.Path), StringComparer.OrdinalIgnoreCase);
                    var truncated = false;
                    foreach (var match in matches)
                    {
                        if (children++ >= MaximumRuleChildren) { truncated = true; break; }
                        var child = ProcessRow(match.Process, match.Process.ApplicationName, match.Process.ApplicationKey, snapshot, id);
                        child.ParentId = id; child.PresentationDepth = grouped ? 1 : 0; Add(child);
                    }
                    var visited = 0;
                    foreach (var app in installed)
                    {
                        if (truncated) break;
                        foreach (var executable in executableIdentity.OrderExecutables(app))
                        {
                            if ((visited++ & 63) == 0) token.ThrowIfCancellationRequested();
                            if (paths.Contains(executable.Path) || !MatchesOffline(rule, app, executable)) continue;
                            if (children++ >= MaximumRuleChildren) { truncated = true; break; }
                            paths.Add(executable.Path);
                            var child = ExecutableRow(executable, app.Name, id); child.ParentId = id; child.PresentationDepth = grouped ? 1 : 0; Add(child);
                        }
                    }
                    if (truncated) Add(new LegacyRow { Id = id + "|limit", Name = L.T("此规则包含更多项目，请缩小规则范围以查看。"), IsChild = true });
                }
            }
            if (grouped && rows.Count > start)
            {
                var first = rows[start]; var open = Open(family.Key);
                if (!open) rows.RemoveRange(start, rows.Count - start);
                rows.Insert(start, new LegacyRow { Id = family.Key, Name = family.Name, Summary = L.F($"{family.Rules.Count} 条规则"),
                    Detail = L.T("单击展开各项规则"), Model = family, Expanded = open, Path = first.Path, IconPath = first.IconPath });
            }
            }
        }
        else
        {
            foreach (var row in AutorunRows(query, category, sort, expanded, collapsed, snapshot, installed,
                autoruns, autorunComplex, display, token)) Add(row);
        }
        }
        catch (RowLimitReachedException)
        {
            rows.Add(new LegacyRow { Id = "limit:page:" + page, Name = L.T("显示数量已达上限，请缩小搜索范围。"), IsChild = true });
        }
        return rows;
    }
    private static bool MatchesOffline(WhitelistRule rule, InstalledApplication app, InstalledExecutable exe) => rule.Kind switch
    {
        RuleKind.Application => rule.Value == app.ApplicationKey || rule.Value == exe.ApplicationKey,
        RuleKind.ExecutablePath => SamePath(rule.Value, exe.Path),
        RuleKind.ProcessName => string.Equals(rule.Value, Path.GetFileName(exe.Path), StringComparison.OrdinalIgnoreCase),
        RuleKind.Directory => exe.Path.StartsWith(rule.Value.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), _ => false
    };
    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static int SourceGroup(AutorunSourceKind source) => source switch { AutorunSourceKind.RegistryRun or AutorunSourceKind.RegistryRunOnce or AutorunSourceKind.PolicyRun => 1, AutorunSourceKind.StartupFolder => 2, AutorunSourceKind.ScheduledTask => 3, AutorunSourceKind.Service => 4, AutorunSourceKind.Driver => 5, AutorunSourceKind.WmiSubscription => 7, AutorunSourceKind.PackagedStartup => 8, _ => 6 };
    private static string SourceName(AutorunSourceKind source) => L.T(new[] { "", "注册表登录项", "启动文件夹", "计划任务", "服务", "驱动", "高级项目", "WMI 订阅", "打包应用启动项" }[SourceGroup(source)]);
    private static string Memory(long bytes) => (bytes / 1024d / 1024).ToString("N1") + " MB";
    private static string CategoryName(RunCategory value) => L.T(value == RunCategory.Visible ? "可见窗口" : value == RunCategory.Minimized ? "最小化" : value == RunCategory.System ? "系统进程" : "后台运行");
    private static string RecoveryBadge(ProcessRecord process, ProcessSnapshot snapshot)
    {
        var hint = WindowRecoveryHints.ForProcess(process, snapshot);
        if (hint is null || hint.Kind == WindowRecoveryKind.AvdRestart && !LegacyRuntimeCapabilities.SupportsAvdRestart || hint.Kind == WindowRecoveryKind.BrowserInspection && !LegacyRuntimeCapabilities.SupportsBrowserLivePreview) return "";
        return " | " + hint.Text;
    }
    private static LegacyRow ProcessRow(ProcessRecord process, string owner, string key, ProcessSnapshot snapshot, string parentId) => new()
    {
        Id = parentId + "|process:" + process.Id + ":" + process.StartTimeUtcTicks, Name = process.Name + " | PID " + process.Id,
        Summary = L.T("所属软件") + ": " + owner + " | " + Memory(process.MemoryBytes) + (process.Windows.Any(w => WindowActions.IsCandidate(process, w, true, key)) ? " | " + L.T("可显示窗口") : RecoveryBadge(process, snapshot)),
        Detail = process.Description + " | " + process.RoleDescription + "\n" + process.Path + "\n" + L.T("父进程") + ": " + process.ParentId + " | " + process.ParentEvidence,
        Path = process.Path, Model = process, ParentId = parentId, IsChild = true, Expanded = true, ApplicationKey = key
    };
    private static LegacyRow ExecutableRow(InstalledExecutable exe, string owner, string parentId, InstalledExecutableRoleIdentity? role = null) => new()
    {
        Id = parentId + "|exe:" + exe.Path.ToUpperInvariant(), Name = exe.Name, Summary = L.T("所属软件") + ": " + owner + " | " + L.T("可执行文件"),
        Path = exe.Path, Detail = exe.Description + "\n" + exe.Path, ParentId = parentId, IsChild = true, Model = exe,
        RoleText = role is null ? "" : InstalledExecutableLabels.Text(role), RoleKind = role is null ? "Unknown" : InstalledExecutableLabels.Kind(role), RoleTooltip = role is null ? "" : InstalledExecutableLabels.Tooltip(role)
    };
    private async Task ApplyIconAsync(LegacyRow row, int version, CancellationToken token)
    {
        var path = row.IconPath.Length > 0 ? row.IconPath : row.Path;
        try { var icon = await _icons.Get(path, token); if (!token.IsCancellationRequested && version == _renderVersion &&
            path.Equals(row.IconPath.Length > 0 ? row.IconPath : row.Path, StringComparison.OrdinalIgnoreCase)) row.Icon = icon; }
        catch (OperationCanceledException) { }
    }
    private async void RowIconLoaded(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: LegacyRow row } && row.Icon is null) await ApplyIconAsync(row, _renderVersion, _life.Token); }
    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    { while (node is not null) { if (node is T result) return result; node = VisualTreeHelper.GetParent(node); } return null; }
    private async void RowClicked(object sender, MouseButtonEventArgs e)
    {
        if (LegacyRowInteraction.BodyContainer(List, e)?.DataContext is not LegacyRow row) return;
        e.Handled = true;
        if (e.ClickCount == 1) await ToggleRowExpansionAsync(row);
    }
    private async void RowChevronClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: LegacyRow row }) await ToggleRowExpansionAsync(row);
    }
    private async Task ToggleRowExpansionAsync(LegacyRow row)
    {
        if (_closed || _busy || !row.CanExpand) return;
        if (row.IsPresentationGroup)
        {
            if (Search.Text.Trim().Length > 0)
            { if (row.Expanded) _searchCollapsed.Add(row.Id); else _searchCollapsed.Remove(row.Id); }
            else
            {
                var key = _page + ":" + row.PresentationPlatformId;
                if (!_collapsedPlatforms.Add(key)) _collapsedPlatforms.Remove(key);
            }
            await RenderAsync(); return;
        }
        if (Search.Text.Trim().Length > 0) { if (row.Expanded) _searchCollapsed.Add(row.Id); else _searchCollapsed.Remove(row.Id); }
        else if (!_expanded.Add(row.Id)) _expanded.Remove(row.Id);
        await RenderAsync();
    }
    private void RowContextMenu(object sender, ContextMenuEventArgs e)
    {
        var row = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as LegacyRow ?? List.SelectedItem as LegacyRow;
        if (row is null) { e.Handled = true; return; }
        if (row.IsPresentationGroup) { e.Handled = true; return; }
        var menu = new ContextMenu();
        void Add(string text, Action action, bool enabled = true) { var item = new MenuItem { Header = L.T(text), IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("显示文件所在目录", () => OpenLocation(row.Path), row.Path.Length > 0);
        if (row.Model is ProcessRecord process)
        {
            Add("显示窗口", async () => await ShowProcessWindow(process, row.ApplicationKey));
            Add("关闭进程", async () => await CloseTargets(new[] { process }));
            if (SensitiveProcessClose.IsSensitiveComponent(process))
                Add("强制关闭敏感进程…", async () => await CloseSensitiveTargetAsync(process), !_busy && !_backend.Policy.EvaluateSensitiveClose(process, _snapshot, RunningRules).Protected);
            else Add("强制关闭", async () => await CloseTargets(new[] { process }, true));
            Add("加入白名单", async () => await KeepRow(row));
        }
        if (row.Model is ApplicationGroup app) { Add("显示窗口", async () => { foreach (var process in app.Processes) if (await ShowProcessWindow(process, app.Key)) break; }); Add("关闭程序", async () => await CloseTargets(app.Processes)); }
        if (row.Model is WhitelistRule rule) Add("删除规则", async () => { if (await Confirm(L.T("删除规则"), rule.Name)) SaveRules(_rules.Where(item => item.Id != rule.Id).ToArray()); });
        List.ContextMenu = menu;
    }
}
