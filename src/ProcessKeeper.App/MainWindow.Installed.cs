using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly InstalledApplicationCatalog _installedCatalog = new();
    private readonly ObservableCollection<InstalledRow> _installedRows = [];
    private readonly Dictionary<string, InstalledApplication> _manualInstalled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedInstalled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedInstalledSearch = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InstalledApplication> _installedApplications = [];
    private bool _installedInitialized, _installedLoaded, _installedScanning, _renderingInstalled;
    private bool _installedRenderQueued;
    private bool _installedScrollPending;
    private bool _installedRenderRunning, _installedRenderDirty, _updatingInstalledDrives;
    private string _installedDrivePreference = "";
    private readonly CancellationTokenSource _installedLifetime = new();
    private CancellationTokenSource? _installedRenderCancellation;
    private readonly DispatcherTimer _installedSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string? _installedSelectedRow;
    private string _installedScanNote = L.T("尚未扫描");
    private IReadOnlyList<InstalledApplication>? _mergedInstalledBase, _mergedInstalledResult;
    private IReadOnlyList<UninstallEntry>? _mergedInstalledRegistrations;
    private InstalledApplication[] _mergedManualInstalled = [];

    private void InitializeInstalledPage()
    {
        InstalledList.ItemsSource = _installedRows;
        UpdateInstalledDrives();
        _installedSearchTimer.Tick += (_, _) => { _installedSearchTimer.Stop(); RenderInstalled(); };
        _installedInitialized = true;
    }

    private void StartInstalledPreload()
    {
        if (_installedInitialized && !_installedLoaded && !_closed) _ = ScanInstalledAsync();
    }

    private void CancelInstalledWork()
    {
        _installedSearchTimer.Stop();
        _installedLifetime.Cancel();
        _installedRenderCancellation?.Cancel();
    }

    private async Task EnsureInstalledLoadedAsync()
    {
        if (!_installedInitialized || _closed) return;
        if (!_installedLoaded) await ScanInstalledAsync();
        RenderInstalled();
    }

    private async void RescanInstalled(object sender, RoutedEventArgs args) => await ScanInstalledAsync();

    private async Task ScanInstalledAsync()
    {
        if (_installedScanning || _closed) return;
        SetInstalledBusy(true);
        try
        {
            var applications = await Task.Run(() => _installedCatalog.Scan(_installedLifetime.Token), _installedLifetime.Token);
            if (_closed) return;
            _installedApplications = applications;
            RequestPresentationCapture();
            InvalidateRuleSearchIndex();
            _installedLoaded = true;
            UpdateInstalledDrives();
            _installedScanNote = L.F($"扫描于 {DateTime.Now:HH:mm:ss}");
            var warnings = _installedCatalog.Warnings;
            if (warnings.Count > 0)
                _installedScanNote += L.F($" | {warnings.Count} 条采集提示");
            ToolTipService.SetToolTip(InstalledStatus, warnings.Count > 0 ? string.Join("\n", warnings.Take(15)) : L.T("从本机安装信息与软件目录读取，不会启动发现的文件。"));
            RenderInstalled();
        }
        catch (OperationCanceledException) when (_installedLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_closed) return;
            _installedScanNote = L.T("扫描未完成，可重新扫描；已读取的列表保留");
            ShowNotice(L.T("已安装应用读取失败"), ex.Message, InfoBarSeverity.Warning);
        }
        finally
        {
            if (!_closed) { SetInstalledBusy(false); RenderInstalled(); }
        }
    }

    private void SetInstalledBusy(bool busy)
    {
        _installedScanning = busy;
        InstalledProgress.IsActive = busy;
        InstalledProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstalledScanButton.IsEnabled = InstalledAddButton.IsEnabled = !busy;
        InstalledStatus.Text = busy ? L.T("正在读取安装信息和可执行文件…") : _installedScanNote;
        foreach (var row in _installedRows)
        {
            if (busy) { row.CanKeep = false; row.Notify(); }
        }
    }

    private IReadOnlyList<InstalledApplication> AllInstalledApplications()
    {
        var registrations = _uninstallView?.InventoryEntries ?? Array.Empty<UninstallEntry>();
        if (_mergedInstalledResult is not null && ReferenceEquals(_mergedInstalledBase, _installedApplications) &&
            ReferenceEquals(_mergedInstalledRegistrations, registrations) && _mergedManualInstalled.SequenceEqual(_manualInstalled.Values)) return _mergedInstalledResult;
        _mergedInstalledBase = _installedApplications; _mergedInstalledRegistrations = registrations;
        _mergedManualInstalled = _manualInstalled.Values.ToArray();
        var source = _installedApplications.Concat(_mergedManualInstalled).GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToArray();
        return _mergedInstalledResult = ApplicationPresentationGroups.MergeInstalled(
            InstalledApplicationCatalog.IncludeVerifiedApplications(source, registrations));
    }

    private void OnUninstallInventoryChanged()
    {
        if (_closed) return;
        InvalidateRuleSearchIndex(); UpdateInstalledDrives(); RefreshWhitelistActionGuards();
        RenderInstalled(); RequestPresentationCapture();
    }

    private void RefreshInstalledRunningState()
    {
        if (_installedInitialized && InstalledPage.Visibility == Visibility.Visible) RenderInstalled();
    }

    private void InstalledSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_installedInitialized) return;
        _collapsedInstalledSearch.Clear();
        _installedRenderCancellation?.Cancel();
        _installedSearchTimer.Stop();
        _installedSearchTimer.Start();
    }

    private void InstalledDriveChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_installedInitialized || _updatingInstalledDrives) return;
        _installedDrivePreference = (InstalledDriveFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        SaveViewPreferences();
        RenderInstalled();
    }

    private void ApplyInstalledDrivePreference(string drive)
    {
        _installedDrivePreference = drive;
        UpdateInstalledDrives();
        if (_installedInitialized) RenderInstalled();
    }

    private void UpdateInstalledDrives()
    {
        _updatingInstalledDrives = true;
        try
        {
            InstalledDriveFilter.Items.Clear();
            InstalledDriveFilter.Items.Add(new ComboBoxItem { Content = L.T("所有盘符"), Tag = "" });
            foreach (var drive in InstalledApplicationSearch.Drives(AllInstalledApplications()))
                InstalledDriveFilter.Items.Add(new ComboBoxItem { Content = drive, Tag = drive });
            var selected = InstalledDriveFilter.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == _installedDrivePreference);
            // Preserve an imported choice until inventory is ready; a missing drive on
            // another computer then falls back to All drives without writing its settings.
            if (selected is null && _installedLoaded) _installedDrivePreference = "";
            InstalledDriveFilter.SelectedItem = selected ?? InstalledDriveFilter.Items[0];
        }
        finally { _updatingInstalledDrives = false; }
    }

    private async void RenderInstalled()
    {
        NativeSelectionTree.For(InstalledList)?.Invalidate(InstalledPage.Visibility == Visibility.Visible);
        if (!_installedInitialized || _closed) return;
        _installedRenderDirty = true;
        _installedRenderCancellation?.Cancel();
        if (_installedRenderRunning || InstalledPage.Visibility != Visibility.Visible) return;
        _installedRenderRunning = true;
        try
        {
            while (_installedRenderDirty && !_closed && InstalledPage.Visibility == Visibility.Visible)
            {
                _installedRenderDirty = false;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_installedLifetime.Token);
                _installedRenderCancellation = cancellation;
                var query = InstalledSearch.Text?.Trim() ?? "";
                var drive = (InstalledDriveFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                var showSystem = ShowSystemToggle.IsChecked == true;
                var snapshot = _snapshot;
                var rules = InstalledRulesForScope;
                var applications = AllInstalledApplications().ToArray();
                var expanded = _expandedInstalled.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var collapsedSearch = _collapsedInstalledSearch.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var canKeep = _configurationHealthy && !_working && !_installedScanning;
                var display = _displayCatalog; var groupPlatforms = _groupGamePlatforms; var hideMicrosoft = _hideMicrosoftApps;
                var collapsedPlatforms = _collapsedInstalledPlatforms.ToHashSet(StringComparer.Ordinal);
                var uninstallEntries = _uninstallView?.InventoryEntries.ToArray() ?? Array.Empty<UninstallEntry>();
                try
                {
                    var result = await Task.Run(() => BuildInstalledRows(applications, snapshot, rules, expanded, collapsedSearch,
                        query, drive, showSystem, canKeep, cancellation.Token, display, groupPlatforms, hideMicrosoft, collapsedPlatforms, uninstallEntries), cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_closed || InstalledPage.Visibility != Visibility.Visible) continue;
                    await ApplyInstalledRowsAsync(result.Rows, result.ApplicationCount, snapshot, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!_closed) ShowNotice(L.T("已安装应用读取失败"), ex.Message, InfoBarSeverity.Warning);
                }
                finally { if (ReferenceEquals(_installedRenderCancellation, cancellation)) _installedRenderCancellation = null; }
            }
        }
        finally { _installedRenderRunning = false; }
    }

    private (List<InstalledRow> Rows, int ApplicationCount) BuildInstalledRows(InstalledApplication[] source,
        ProcessSnapshot snapshot, IReadOnlyList<WhitelistRule> rules, HashSet<string> expanded, HashSet<string> collapsedSearch,
        string query, string drive, bool showSystem, bool canKeep, CancellationToken cancellationToken,
        ApplicationDisplayCatalog? display = null, bool groupPlatforms = false, bool hideMicrosoft = false, HashSet<string>? collapsedPlatforms = null,
        IReadOnlyList<UninstallEntry>? uninstallEntries = null)
    {
        var hasSnapshot = snapshot.CapturedAt != DateTimeOffset.MinValue;
        var search = new InstalledApplicationSearch(snapshot.Processes, showSystem);
        // The catalog can contain thousands of files: group the snapshot once, then
        // perform exact path lookups instead of rescaning every PID for every file.
        var byPath = snapshot.Processes.Where(p => p.Path.Length > 0)
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderBy(p => p.Id).ToArray(), StringComparer.OrdinalIgnoreCase);
        display ??= ApplicationDisplayCatalog.Empty;
        var applications = source.Where(a => { cancellationToken.ThrowIfCancellationRequested(); return (!hideMicrosoft || !display.IsMicrosoft(a)) &&
            (search.Matches(a, query, drive) || search.Matches(a, "", drive) && MatchesPlatformQuery(query, display.FindGame(a), display.FindGamePlatform(a))); })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var next = new List<InstalledRow>();
        var executableIdentity = new InstalledExecutableIdentity(uninstallEntries);
        bool IsComponentKept(InstalledExecutable executable) => ProtectionPolicy.IsDirectlyWhitelisted(
            new ProcessRecord { Name = Path.GetFileName(executable.Path), Path = executable.Path, ApplicationKey = executable.ApplicationKey }, rules);
        foreach (var app in applications)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var componentProcesses = app.Executables.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(path => path, path => byPath.GetValueOrDefault(path) ?? [], StringComparer.OrdinalIgnoreCase);
            var running = componentProcesses.Values.SelectMany(p => p).DistinctBy(p => (p.Id, p.StartTimeUtcTicks)).ToArray();
            var hidden = showSystem ? 0 : running.Count(p => p.Category == RunCategory.System);
            var kept = app.Executables.Count > 0 ? app.Executables.All(IsComponentKept) : app.ApplicationKey.Length > 0 &&
                ProtectionPolicy.IsDirectlyWhitelisted(new ProcessRecord { ApplicationKey = app.ApplicationKey }, rules);
            var explicitExpansion = expanded.Contains(app.Id);
            var appMatch = query.Length == 0 || InstalledApplicationSearch.MatchesApplication(app, query) || MatchesPlatformQuery(query, display.FindGame(app), display.FindGamePlatform(app));
            var componentMatch = query.Length > 0 && app.Executables.Any(e => search.MatchesComponent(e, query));
            var parent = new InstalledRow
            {
                RowKey = "app:" + app.Id, ApplicationId = app.Id, Kind = InstalledRowKind.Application,
                Name = display.FindGame(app)?.Name is { Length: > 0 } gameName ? gameName : app.Name, IsExpanded = explicitExpansion || (componentMatch && !collapsedSearch.Contains(app.Id)),
                Summary = L.F($"{(app.Publisher.Length > 0 ? app.Publisher : L.T("发布者未提供"))} | {app.Executables.Count} 个可执行组件 | {running.Length} 个关联进程"),
                Status = app.Executables.Count == 0 ? L.T("未读取到可执行组件；按已识别的程序身份保留") : !hasSnapshot ? L.T("尚无进程快照 | 可以提前加入白名单") : hidden > 0 ? L.F($"{hidden} 个系统 / 服务进程已隐藏") : running.Length == 0 ? L.T("当前快照未匹配到运行进程 | 可以提前加入白名单") : L.T("运行状态来自进程快照"),
                Details = InstalledApplicationDetails(app),
                IconPath = display.ResolveIconPath(app, executableIdentity), IsKept = kept,
                CanKeep = !kept && canKeep && (app.Executables.Count > 0 || app.ApplicationKey.Length > 0)
            };
            if (kept) parent.Status = L.T("已保留") + " | " + parent.Status;
            next.Add(parent);
            if (!parent.IsExpanded) continue;
            if (app.Executables.Count == 0) continue;
            foreach (var executable in executableIdentity.OrderExecutables(app))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!explicitExpansion && !appMatch && !search.MatchesComponent(executable, query)) continue;
                var processes = componentProcesses[executable.Path];
                var visibleProcesses = showSystem ? processes : processes.Where(p => p.Category != RunCategory.System).ToArray();
                var hiddenProcesses = processes.Length - visibleProcesses.Length;
                if (!explicitExpansion && !appMatch && !InstalledApplicationSearch.MatchesExecutable(executable, query))
                    visibleProcesses = visibleProcesses.Where(p => InstalledApplicationSearch.MatchesProcess(p, query)).ToArray();
                var componentKept = IsComponentKept(executable);
                var role = executableIdentity.Classify(app, executable);
                var componentKey = InstalledExecutableRowKey(app.Id, executable.Path);
                var componentExpanded = expanded.Contains(componentKey) || query.Length > 0 &&
                    visibleProcesses.Any(process => InstalledApplicationSearch.MatchesProcess(process, query)) && !collapsedSearch.Contains(componentKey);
                var component = new InstalledRow
                {
                    RowKey = componentKey, ApplicationId = app.Id, ExecutablePath = executable.Path, Kind = InstalledRowKind.Executable,
                    HasChildren = visibleProcesses.Length > 0, IsExpanded = componentExpanded,
                    Name = executable.Name, Summary = (executable.Description.Length > 0 ? executable.Description : L.T("可执行文件")) +
                        (app.Installations.Count > 1 ? " | " + Path.GetDirectoryName(executable.Path) : ""),
                    Status = !hasSnapshot ? L.T("尚无进程快照（扫描时文件存在，未试运行）") : processes.Length > 0 ? L.F($"正在运行 | {processes.Length} 个进程") + (hiddenProcesses > 0 ? L.F($"（{hiddenProcesses} 个系统 / 服务已隐藏）") : "") : L.T("未运行（当前快照未匹配，文件未试运行）"),
                    Details = L.F($"可执行文件：{executable.Name}\n说明：{executable.Description}\n完整路径：{executable.Path}\n程序身份：{executable.ApplicationKey}\n\n发现文件不代表它正在运行。下方 PID 仅在当前快照中存在相同完整路径时显示。单独保留组件会添加完整路径规则。"),
                    IconPath = executable.Path, IsKept = componentKept, CanKeep = !componentKept && canKeep,
                    RoleText = InstalledExecutableLabels.Text(role), RoleKind = InstalledExecutableLabels.Kind(role), RoleTooltip = InstalledExecutableLabels.Tooltip(role)
                };
                if (componentKept) component.Status = L.T("已保留") + " | " + component.Status;
                next.Add(component);
                if (!componentExpanded) continue;
                foreach (var process in visibleProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var recovery = WindowRecoveryHints.ForProcess(process, snapshot);
                    next.Add(new InstalledRow
                    {
                        RowKey = $"pid:{app.Id}:{process.Id}:{process.StartTimeUtcTicks}", ApplicationId = app.Id,
                        ExecutablePath = executable.Path, ProcessId = process.Id, ProcessStartTicks = process.StartTimeUtcTicks, Kind = InstalledRowKind.Process,
                        Name = $"{process.Name} | PID {process.Id}", Summary = L.F($"所属软件：{app.Name}") + $" | {CategoryLabel(process.Category)} | {Memory(process.MemoryBytes)} | {process.RoleDescription}",
                        Status = L.T("正在运行 | 与上方组件的完整路径相同"),
                        RecoveryText = recovery?.Text ?? "", RecoveryTooltip = recovery?.Tooltip ?? "",
                        Details = L.F($"运行进程：{process.Name}\nPID：{process.Id} | 父 PID：{process.ParentId}\n用途：{process.RoleDescription}\n状态：{CategoryLabel(process.Category)}\n内存：{Memory(process.MemoryBytes)}\n路径：{process.Path}\n保留判定：{_policy.Evaluate(process, snapshot, rules).Reason}"),
                        IconPath = process.Path
                    });
                }
            }
        }
        return (GroupInstalledRows(next, applications, display, groupPlatforms, collapsedPlatforms ?? [], query), applications.Length);
    }

    private static string InstalledApplicationDetails(InstalledApplication app)
    {
        var members = app.Installations.Count > 0 ? app.Installations : new[] { app };
        return string.Join("\n\n", members.Select(member => L.F($"程序：{member.Name}\n发布者声明：{member.Publisher}\n安装位置：{member.InstallLocation}\n识别依据：{member.IdentityEvidence}\n程序身份：{member.ApplicationKey}\n\n这里只列出扫描范围内可读取的可执行文件，不保证覆盖全部组件。运行进程仅按完整文件路径精确关联，不会将相同程序身份的 PID 重复分配给不同文件。")));
    }

    private async Task ApplyInstalledRowsAsync(List<InstalledRow> next, int applicationCount, ProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        _renderingInstalled = true;
        try
        {
            var desired = next.Select(r => r.RowKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = _installedRows.Count - 1; i >= 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!desired.Contains(_installedRows[i].RowKey)) _installedRows.RemoveAt(i);
                if (i % 64 == 0)
                {
                    await Task.Delay(1, cancellationToken);
                    if (_closed || InstalledPage.Visibility != Visibility.Visible) return;
                }
            }
            var existingRows = _installedRows.ToDictionary(row => row.RowKey, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < next.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var incoming = next[i];
                existingRows.TryGetValue(incoming.RowKey, out var row);
                if (row is null) { row = incoming; _installedRows.Insert(i, row); }
                else
                {
                    if (!ReferenceEquals(_installedRows[i], row)) NativeListSelection.Move(InstalledList, _installedRows, _installedRows.IndexOf(row), i, item => item.RowKey);
                    row.Name = incoming.Name; row.Summary = incoming.Summary; row.Status = incoming.Status; row.Details = incoming.Details;
                    row.RecoveryText = incoming.RecoveryText; row.RecoveryTooltip = incoming.RecoveryTooltip;
                    row.RoleText = incoming.RoleText; row.RoleKind = incoming.RoleKind; row.RoleTooltip = incoming.RoleTooltip;
                    row.IsExpanded = incoming.IsExpanded; row.HasChildren = incoming.HasChildren; row.IsKept = incoming.IsKept; row.CanKeep = incoming.CanKeep;
                    row.IsPresentationGroup = incoming.IsPresentationGroup; row.PresentationDepth = incoming.PresentationDepth; row.PresentationPlatformId = incoming.PresentationPlatformId;
                }
                row.CanKeep = incoming.CanKeep && !_working && !_installedScanning;
                row.IconPath = incoming.IconPath;
                row.Notify();
                if (i % 64 == 63)
                {
                    await Task.Delay(1, cancellationToken);
                    if (_closed || InstalledPage.Visibility != Visibility.Visible) return;
                }
            }
            var selected = _installedRows.FirstOrDefault(r => r.RowKey == _installedSelectedRow);
            // Keep the native selected-item set intact while refreshing the focused inspector row.
            _installedSelectedRow = selected?.RowKey;
            InstalledFacts.Text = selected?.Details ?? L.T("选择程序、可执行组件或运行进程，查看来源和路径。");
            if (_installedScrollPending && selected is not null)
            {
                _installedScrollPending = false;
                InstalledList.ScrollIntoView(selected);
            }
            InstalledEmpty.Visibility = applicationCount == 0 && !_installedScanning ? Visibility.Visible : Visibility.Collapsed;
            if (!_installedScanning) InstalledStatus.Text = L.F($"{applicationCount} 个程序 | {_installedScanNote} | ") +
                (snapshot.CapturedAt != DateTimeOffset.MinValue ? L.F($"进程更新于 {snapshot.CapturedAt.LocalDateTime:HH:mm:ss}") : L.T("尚未取得进程快照")) +
                (LiveToggle.IsOn ? L.T(" | 实时更新") : L.T(" | 实时更新已暂停"));
        }
        finally { _renderingInstalled = false; QueueVisibleIcons(); RefreshSelectionButtons(); }
    }

    private void InstalledChevronClicked(object sender, RoutedEventArgs args)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRow row) ToggleInstalledRow(row);
    }
    private void ToggleInstalledRow(InstalledRow row)
    {
        if (_working || _renderingInstalled || row.Kind == InstalledRowKind.Process || row.Kind == InstalledRowKind.Executable && !row.HasChildren) return;
        if (row.IsPresentationGroup)
        {
            if (!_collapsedInstalledPlatforms.Add(row.PresentationPlatformId)) _collapsedInstalledPlatforms.Remove(row.PresentationPlatformId);
            RenderInstalled(); return;
        }
        if (row.Kind == InstalledRowKind.Application || row.HasChildren)
        {
            var expansionKey = row.Kind == InstalledRowKind.Application ? row.ApplicationId : row.RowKey;
            if (row.IsExpanded)
            {
                _expandedInstalled.Remove(expansionKey);
                _collapsedInstalledSearch.Add(expansionKey);
            }
            else
            {
                _expandedInstalled.Add(expansionKey);
                _collapsedInstalledSearch.Remove(expansionKey);
            }
            if (_installedRenderQueued) return;
            _installedRenderQueued = DispatcherQueue.TryEnqueue(() =>
            {
                _installedRenderQueued = false;
                if (!_closed) RenderInstalled();
            });
        }
    }

    private void InstalledSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (NativeSelectionTree.For(InstalledList)?.IsApplying == true) return;
        RefreshSelectionButtons();
    }

    private void KeepInstalled(object sender, RoutedEventArgs args)
    {
        var clicked = (sender as FrameworkElement)?.DataContext as InstalledRow;
        var previousRules = _rules;
        try
        {
        if (!_installedInitialized || _renderingInstalled || _working || _installedScanning || !_configurationHealthy || sender is not CheckBox { Tag: string key } checkbox || clicked?.CanKeep != true) return;
        var requestedSelection = checkbox.IsChecked == true;
        var row = _installedRows.FirstOrDefault(r => r.RowKey == key);
        var app = row is null ? null : AllInstalledApplications().FirstOrDefault(a => a.Id == row.ApplicationId);
        if (app is null || row is null) return;
        try
        {
            var requested = row.Kind == InstalledRowKind.Application ? InstalledApplicationCatalog.GetAppRules(app) :
                new[] { new WhitelistRule { Name = app.Name + " | " + Path.GetFileName(row.ExecutablePath), Kind = RuleKind.ExecutablePath, Value = row.ExecutablePath } };
            if (requested.Count == 0) { ShowNotice(L.T("无法添加规则"), L.T("没有可核实的程序身份或可执行文件。可重新扫描或手动添加文件。"), InfoBarSeverity.Warning); RenderInstalled(); return; }
            var next = _rules.ToList();
            foreach (var rule in requested)
            {
                var existingIndex = next.FindIndex(r => r.Kind == rule.Kind && r.Value.Equals(rule.Value, StringComparison.OrdinalIgnoreCase) && r.IncludeDescendants == rule.IncludeDescendants);
                if (existingIndex >= 0) next[existingIndex] = next[existingIndex] with { Enabled = true };
                else next.Add(rule with { Id = "installed-" + Guid.NewGuid().ToString("N"), Enabled = true });
            }
            CommitRules(next, L.T("从已安装应用加入白名单：") + row.Name);
            if (!ReferenceEquals(previousRules, _rules)) NativeSelectionTree.SetFromAction(checkbox, requestedSelection);
        }
        catch (Exception ex) { ShowNotice(L.T("未添加白名单"), ex.Message, InfoBarSeverity.Warning); }
        RenderInstalled();
        }
        finally
        {
            // Restore rejected/no-op clicks immediately. A successful add is rendered asynchronously;
            // keep its just-checked visual state until that render publishes the updated model.
            if (clicked is not null && (clicked.IsKept || ReferenceEquals(previousRules, _rules)))
            {
                clicked.NotifyKeepState();
                if (sender is CheckBox checkbox) RestoreActionBinding(checkbox, nameof(InstalledRow.IsKept));
            }
        }
    }

    private async void AddInstalledExecutable(object sender, RoutedEventArgs args) => await AddManualInstalledAsync(false);
    private async void AddInstalledDirectory(object sender, RoutedEventArgs args) => await AddManualInstalledAsync(true);

    private async Task AddManualInstalledAsync(bool directory)
    {
        if (_dialogOpen || _working || _installedScanning) return;
        string? path = null;
        _dialogOpen = true;
        try
        {
            if (directory)
            {
                var result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync();
                path = result?.Path;
            }
            else
            {
                var picker = new FileOpenPicker(AppWindow.Id);
                picker.FileTypeFilter.Add(".exe");
                path = (await picker.PickSingleFileAsync())?.Path;
            }
        }
        catch (Exception ex) { ShowNotice(L.T("无法读取选择"), ex.Message, InfoBarSeverity.Warning); }
        finally { _dialogOpen = false; }
        if (path is null || _closed) return;
        SetInstalledBusy(true);
        try
        {
            var app = await Task.Run(() => directory ? _installedCatalog.ScanDirectory(path, _installedLifetime.Token) : _installedCatalog.FromExecutable(path, _installedLifetime.Token), _installedLifetime.Token);
            if (_closed) return;
            if (app is null) { ShowNotice(L.T("没有可添加的程序"), _installedCatalog.Warnings.Count > 0 ? string.Join("\n", _installedCatalog.Warnings.Take(5)) : L.T("未找到可读取的 EXE。请选择程序的实际安装目录或直接添加 EXE 文件。"), InfoBarSeverity.Informational); return; }
            _manualInstalled[app.Id] = app;
            RequestPresentationCapture();
            InvalidateRuleSearchIndex();
            _expandedInstalled.Add(app.Id);
            _installedLoaded = true;
            InstalledSearch.Text = "";
            UpdateInstalledDrives();
            InstalledDriveFilter.SelectedIndex = 0;
            _installedSelectedRow = "app:" + app.Id;
            _installedScrollPending = true;
            RenderInstalled();
            ShowNotice(L.T("已添加到软件列表"), _installedCatalog.Warnings.Count > 0 ? string.Join("\n", _installedCatalog.Warnings.Take(5)) : L.T("展开查看组件，再勾选需要保留的项目。"), _installedCatalog.Warnings.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) when (_installedLifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_closed) ShowNotice(L.T("添加失败"), ex.Message, InfoBarSeverity.Warning); }
        finally { if (!_closed) { SetInstalledBusy(false); RenderInstalled(); } }
    }

    private void ManageInstalledRules(object sender, RoutedEventArgs args)
    {
        var item = Navigation.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => i.Tag?.ToString() == "rules");
        if (item is not null) Navigation.SelectedItem = item;
    }

    private void InstalledSystemVisibilityChanged(object sender, RoutedEventArgs args)
    {
        if (!_installedInitialized || sender is not CheckBox box) return;
        ShowSystemToggle.IsChecked = box.IsChecked == true;
        SystemVisibilityChanged(ShowSystemToggle, args);
        RenderInstalled();
    }
}
