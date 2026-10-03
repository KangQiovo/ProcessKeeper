using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly HashSet<string> _expandedRules = new(StringComparer.Ordinal);
    private bool _ruleRenderQueued;
    private bool _ruleRendering;
    private long _ruleRevision;
    private RuleSearchIndex? _ruleSearchIndex;

    private void InvalidateRuleSearchIndex()
    {
        _ruleSearchIndex = null;
        _autorunsView?.UpdateInstalled(AllInstalledApplications().ToArray());
        RenderRules();
    }

    private void RuleChevronClicked(object sender, RoutedEventArgs args)
    {
        if (_working || (sender as FrameworkElement)?.DataContext is not RuleRow { IsProcess: false } row) return;
        if (row.IsExpanded)
        {
            _expandedRules.Remove(row.Id);
            if (!string.IsNullOrWhiteSpace(RulesSearch.Text)) _searchCollapsedRules.Add(row.Id);
        }
        else { _searchCollapsedRules.Remove(row.Id); _expandedRules.Add(row.Id); }
        // Finish ListView's input/selection callback before changing its item collection.
        if (_ruleRenderQueued) return;
        _ruleRenderQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _ruleRenderQueued = false;
            if (!_closed) RenderRules();
        })) _ruleRenderQueued = false;
    }

    private void RuleItemClicked(object sender, ItemClickEventArgs args) => RefreshSelectionButtons();

    private async void EditRuleFromMenu(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem { Tag: string id })
            await EditRuleDialog(_rules.FirstOrDefault(rule => rule.Id == id));
    }

    private void RemoveRuleFromMenu(object sender, RoutedEventArgs args)
    {
        if (sender is MenuFlyoutItem { Tag: string id } && _rules.FirstOrDefault(rule => rule.Id == id) is { } rule)
            CommitRules(_rules.Where(item => item.Id != id).ToArray(), L.T("移除规则：") + WhitelistStore.GetDisplayName(rule));
    }

    private void RenderRules()
    {
        NativeSelectionTree.For(RulesList)?.Invalidate(RulesPage.Visibility == Visibility.Visible);
        _ruleRevision++;
        if (!_ready || _closed || _ruleRendering || RulesPage.Visibility != Visibility.Visible) return;
        _ = RenderRulesAsync();
    }

    private async Task RenderRulesAsync()
    {
        _ruleRendering = true;
        try
        {
            long revision;
            do
            {
                revision = _ruleRevision;
                await RenderRulesOnceAsync(revision);
            } while (!_closed && RulesPage.Visibility == Visibility.Visible && revision != _ruleRevision);
        }
        catch (Exception ex) { if (!_closed) ShowNotice(L.T("白名单读取失败"), ex.Message, InfoBarSeverity.Warning); }
        finally { _ruleRendering = false; }
    }

    private async Task RenderRulesOnceAsync(long revision)
    {
        var showSystem = ShowSystemToggle.IsChecked == true;
        var snapshot = _snapshot;
        var rules = _rules;
        var expanded = _expandedRules.ToHashSet(StringComparer.Ordinal);
        var collapsedSearch = _searchCollapsedRules.ToHashSet(StringComparer.Ordinal);
        var query = RulesSearch.Text?.Trim() ?? "";
        var installed = AllInstalledApplications().ToArray();
        var display = _displayCatalog;
        var searchIndex = _ruleSearchIndex ??= new RuleSearchIndex(installed);
        var rows = await Task.Run(() =>
        {
        var _snapshot = snapshot;
        var _rules = rules;
        var _expandedRules = expanded;
        ProtectionDecision Decision(ProcessRecord process) => _policy.Evaluate(process, snapshot, rules);
        var matcher = new RuleProcessMatcher(_snapshot);
        var components = installed.SelectMany(app => app.Executables)
            .DistinctBy(executable => executable.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var byApplication = components.Where(e => e.ApplicationKey.Length > 0)
            .GroupBy(e => e.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
        var byName = components.GroupBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.OrdinalIgnoreCase);
        var next = new List<RuleRow>();
        var offlineMatches = query.Length > 0 ? searchIndex.MatchingRuleIds(rules, query) : [];
        foreach (var group in ApplicationPresentationGroups.GroupRules(_rules, installed, snapshot))
        {
        var groupStart = next.Count;
        foreach (var rule in group.Rules)
        {
            var matches = matcher.Match(rule);
            var visible = matches.Where(match => showSystem || match.Process.Category != RunCategory.System)
                .OrderBy(match => match.IsInherited).ThenBy(match => match.Process.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(match => match.Process.Id).ToArray();
            var hidden = matches.Count - visible.Length;
            if (query.Length > 0 && !ApplicationSearch.MatchesRule(rule, WhitelistStore.GetDisplayName(rule), visible, query) &&
                !offlineMatches.Contains(rule.Id)) continue;
            var queryMatches = query.Length > 0 ? visible.Where(match => ApplicationSearch.MatchesProcess(match.Process, query)).ToArray() : [];
            var searchExpanded = queryMatches.Length > 0 && !collapsedSearch.Contains(rule.Id);
            var status = !rule.Enabled ? L.F($"已停用 | 匹配预览 {visible.Length} 个运行进程") :
                matches.Count == 0 ? L.T("当前未匹配 | 未运行、未安装或身份不可读") : L.F($"当前匹配 {visible.Length} 个运行进程");
            if (hidden > 0) status += L.F($" | {hidden} 个系统 / 服务进程已隐藏");
            var scope = RulePortability.IsPortable(rule) ? L.T("通用程序身份") : L.T("本机规则 | 跨电脑需核对");
            var inheritance = rule.IncludeDescendants ? L.T(" | 包含核实的子进程") : "";
            var iconPath = matches.OrderByDescending(match => match.Process.HasVisibleWindow)
                .Select(match => match.Process.Path).FirstOrDefault(path => path.Length > 0) ?? "";
            if (iconPath.Length == 0)
                iconPath = RuleIconPath(rule, components, byApplication, byName);
            var locationPath = iconPath;
            iconPath = display.ResolveGameIconPath(iconPath);
            next.Add(new RuleRow
            {
                Id = rule.Id, RowKey = "rule:" + rule.Id, Name = WhitelistStore.GetDisplayName(rule), Enabled = rule.Enabled,
                PresentationDepth = group.Rules.Count > 1 ? 1 : 0,
                IsExpanded = _expandedRules.Contains(rule.Id) || searchExpanded, IconPath = iconPath, LocationPath = locationPath,
                Detail = $"{KindLabel(rule.Kind)} | {rule.Value}", MatchText = status,
                Tooltip = $"{WhitelistStore.GetDisplayName(rule)}\n{scope}{inheritance}\n{KindLabel(rule.Kind)}：{rule.Value}\n{status}\n" +
                    (rule.Enabled ? L.T("匹配表示规则关联。系统保护和其他规则的实际判定见展开后的进程。") : L.T("此规则已停用，展开仅预览关联，不会由此规则保留进程。"))
            });
            if (!_expandedRules.Contains(rule.Id) && !searchExpanded) continue;
            foreach (var match in _expandedRules.Contains(rule.Id) ? visible : queryMatches)
            {
                var process = match.Process;
                var recovery = WindowRecoveryHints.ForProcess(process, _snapshot);
                var relation = match.IsInherited ? L.F($"核实的子进程 | 匹配父级 PID {match.MatchedAncestorId}") : L.T("直接匹配规则");
                var decision = Decision(process);
                var protection = rule.Enabled ? (decision.Protected ? L.T("当前保护：") + decision.Reason : L.T("当前可在确认后关闭")) :
                    L.T("匹配预览 | 未由此规则保护") + (decision.Protected ? L.T(" | 当前保护：") + decision.Reason : "");
                next.Add(new RuleRow
                {
                    Id = rule.Id, RowKey = $"rule:{rule.Id}|pid:{process.Id}:{process.StartTimeUtcTicks}",
                    PresentationDepth = group.Rules.Count > 1 ? 1 : 0,
                    ProcessId = process.Id, ProcessStartTicks = process.StartTimeUtcTicks, IconPath = process.Path, LocationPath = process.Path,
                    Name = $"{process.Name} | PID {process.Id}", Enabled = rule.Enabled,
                    Detail = L.F($"所属软件：{process.ApplicationName}") + $" | {CategoryLabel(process.Category)} | {Memory(process.MemoryBytes)} | {relation}",
                    MatchText = protection,
                    RecoveryText = recovery?.Text ?? "", RecoveryTooltip = recovery?.Tooltip ?? "",
                    Tooltip = $"{process.ApplicationName}\n{process.Name} | PID {process.Id}\n{process.RoleDescription}\n{process.Path}\n{relation}\n{protection}"
                });
            }
        }
        if (group.Rules.Count > 1 && next.Count > groupStart)
        {
            var first = next[groupStart];
            var open = expanded.Contains(group.Key) || query.Length > 0 && !collapsedSearch.Contains(group.Key);
            if (!open) next.RemoveRange(groupStart, next.Count - groupStart);
            next.Insert(groupStart, new RuleRow { Id = group.Key, RowKey = group.Key, Name = group.Name,
                IsApplicationGroup = true, IsExpanded = open, IconPath = first.IconPath, LocationPath = first.LocationPath,
                Detail = L.F($"{group.Rules.Count} 条规则"), MatchText = L.T("单击展开各项规则"), Tooltip = group.Name });
        }
        }
        return next;
        });
        if (_closed || revision != _ruleRevision || RulesPage.Visibility != Visibility.Visible) return;
        var wasRendering = _rendering;
        _rendering = true;
        try
        {
            var desired = rows.Select(row => row.RowKey).ToHashSet(StringComparer.Ordinal);
            for (var index = _ruleRows.Count - 1; index >= 0; index--)
            {
                if (!desired.Contains(_ruleRows[index].RowKey)) _ruleRows.RemoveAt(index);
                if (index % 64 == 0)
                {
                    _rendering = wasRendering;
                    await Task.Yield();
                    if (_closed || revision != _ruleRevision || RulesPage.Visibility != Visibility.Visible) return;
                    _rendering = true;
                }
            }
            var existing = _ruleRows.ToDictionary(row => row.RowKey, StringComparer.Ordinal);
            for (var index = 0; index < rows.Count; index++)
            {
                if (index > 0 && index % 64 == 0)
                {
                    _rendering = wasRendering;
                    await Task.Yield();
                    if (_closed || revision != _ruleRevision || RulesPage.Visibility != Visibility.Visible) return;
                    _rendering = true;
                }
                var candidate = rows[index];
                if (!existing.TryGetValue(candidate.RowKey, out var row))
                {
                    candidate.Notify();
                    _ruleRows.Insert(index, candidate);
                    continue;
                }
                if (!ReferenceEquals(_ruleRows[index], row)) NativeListSelection.Move(RulesList, _ruleRows, _ruleRows.IndexOf(row), index, item => item.RowKey);
                row.Name = candidate.Name;
                row.Detail = candidate.Detail;
                row.MatchText = candidate.MatchText;
                row.RecoveryText = candidate.RecoveryText;
                row.RecoveryTooltip = candidate.RecoveryTooltip;
                row.Tooltip = candidate.Tooltip;
                row.Enabled = candidate.Enabled;
                row.IsApplicationGroup = candidate.IsApplicationGroup;
                row.PresentationDepth = candidate.PresentationDepth;
                row.IsExpanded = candidate.IsExpanded;
                row.LocationPath = candidate.LocationPath;
                if (!string.Equals(row.IconPath, candidate.IconPath, StringComparison.OrdinalIgnoreCase))
                {
                    row.IconPath = candidate.IconPath;
                    row.IconRequested = false;
                    row.Icon = null;
                    row.NotifyIcon();
                }
                row.Notify();
            }
            _expandedRules.IntersectWith(_rules.Select(rule => rule.Id).Concat(ApplicationPresentationGroups.GroupRules(_rules, installed, snapshot).Select(group => group.Key)));
            RulesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _rendering = wasRendering; QueueVisibleIcons(); RefreshSelectionButtons(); }
    }

    private static string RuleIconPath(WhitelistRule rule, IReadOnlyList<InstalledExecutable> components,
        IReadOnlyDictionary<string, string> byApplication, IReadOnlyDictionary<string, string> byName) => rule.Kind switch
    {
        RuleKind.ExecutablePath => rule.Value,
        RuleKind.Application when byApplication.TryGetValue(rule.Value, out var path) => path,
        RuleKind.Application when rule.Value.StartsWith("path:", StringComparison.OrdinalIgnoreCase) => rule.Value[5..],
        RuleKind.ProcessName when byName.TryGetValue(rule.Value, out var path) => path,
        RuleKind.Directory => components.FirstOrDefault(executable => executable.Path.StartsWith(
            rule.Value.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))?.Path ?? "",
        _ => ""
    };
}
