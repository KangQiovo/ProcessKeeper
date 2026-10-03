using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public partial class MainWindow
{
    private Task<bool> Confirm(string title, string detail) => _backend.Confirm?.Invoke(title, detail) ?? Task.FromResult(MessageBox.Show(this, detail, "Process Keeper | " + title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK);
    private void OpenLocation(string path)
    {
        try
        {
            if (_backend.OpenLocation is not null) { _backend.OpenLocation(path); return; }
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", '"' + path + '"') { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            else Notice(L.T("文件或目录不存在。"));
        }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private void SaveRules(IReadOnlyList<WhitelistRule> rules)
    {
        if (!_rulesReadable) { Notice(L.T("白名单配置无法读取，原文件未修改。")); return; }
        try { new WhitelistStore(_directory).Save(rules); _rules = rules; NotifyProfilesRulesChanged(); Log(L.T("白名单已保存")); _ = RenderAsync(); }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private async void RowAction(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: LegacyRow row }) return;
        try
        {
            await ExecuteRowActionAsync(row);
        }
        catch (Exception error) { if (!_closed) Notice(error.Message); }
        finally
        {
            row.IsActionChecked = RowActionChecked(row);
            if (row.Model is ApplicationGroup or InstalledApplication) row.ActionLabel = row.IsActionChecked ? L.T("已保留") : L.T("保留");
            row.Notify();
            row.NotifyActionState();
        }
    }
    private async Task ExecuteRowActionAsync(LegacyRow row)
    {
        if (_busy || !row.CanAct) return;
        if (row.Model is AutorunEntry entry)
        {
            if (entry.Enabled == true && IsAutorunWhitelisted(entry)) { Notice(L.T("白名单保护已阻止停用")); return; }
            if (!entry.CanChange || !entry.Enabled.HasValue)
            {
                var detail = entry.Name + "\n" + entry.Location + "\n\n" + (string.IsNullOrWhiteSpace(entry.ReadOnlyReason) ? L.T("状态未知") : entry.ReadOnlyReason);
                if (_backend.ShowInformation is not null) _backend.ShowInformation(L.T("查看原因"), detail);
                else MessageBox.Show(this, detail, "Process Keeper | " + L.T("查看原因"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!entry.CanChange || !entry.Enabled.HasValue || !await Confirm(L.T("更改自启动"), entry.Name + "\n" + entry.Location + "\n" + L.T("更改下次触发时的启动行为，不结束当前进程。"))) return;
            if (entry.Enabled == true && IsAutorunWhitelisted(entry)) { Notice(L.T("白名单保护已阻止停用")); return; }
            _busy = true;
            try { var result = await Task.Run(() => _backend.ChangeAutorun(entry, !entry.Enabled.Value, _life.Token)); Log(entry.Name + " | " + result.Message); Notice(result.Message, result.Success); await ScanAutorunsAsync(); }
            catch (Exception ex) { Log(L.T("自启动更改未确认") + " | " + ex.Message); Notice(ex.Message); }
            finally { _busy = false; await RenderAsync(); }
        }
        else if (row.Model is WhitelistRule rule)
        {
            if (await Confirm(L.T("白名单"), rule.Name + " | " + row.ActionLabel)) SaveRules(_rules.Select(item => item.Id == rule.Id ? rule with { Enabled = !rule.Enabled } : item).ToArray());
        }
        else if (!row.IsActionChecked) await KeepRow(row);
    }
    private bool RowActionChecked(LegacyRow row) => row.Model switch
    {
        WhitelistRule rule => _rules.FirstOrDefault(item => item.Id == rule.Id)?.Enabled ?? rule.Enabled,
        AutorunEntry entry => _autoruns.Entries.FirstOrDefault(item => item.Id == entry.Id)?.Enabled ?? entry.Enabled == true,
        ApplicationGroup app => app.Processes.Count > 0 && app.Processes.All(process => _backend.Policy.Evaluate(process, _snapshot, RunningRules).Protected),
        InstalledApplication app => InstalledApplicationIsKept(app, _whitelistScope.Installed ? _rules : Array.Empty<WhitelistRule>()),
        _ => false
    };
    private static bool InstalledApplicationIsKept(InstalledApplication app, IReadOnlyList<WhitelistRule> rules) => app.Executables.Count > 0
        ? app.Executables.All(executable => ProtectionPolicy.IsDirectlyWhitelisted(new ProcessRecord { Name = Path.GetFileName(executable.Path), Path = executable.Path, ApplicationKey = executable.ApplicationKey }, rules))
        : app.ApplicationKey.Length > 0 && ProtectionPolicy.IsDirectlyWhitelisted(new ProcessRecord { ApplicationKey = app.ApplicationKey }, rules);
    private async Task KeepRow(LegacyRow row)
    {
        if (!_rulesReadable) return;
        IReadOnlyList<WhitelistRule> incoming;
        if (row.Model is InstalledApplication installed) incoming = InstalledApplicationCatalog.GetAppRules(installed);
        else if (row.Model is ApplicationGroup app) incoming = ApplicationPresentationGroups.GetRunningRules(app);
        else if (row.Model is ProcessRecord process) incoming = new[] { new WhitelistRule { Name = process.Name, Kind = RuleKind.ExecutablePath, Value = process.Path } };
        else return;
        if (incoming.Count == 0) { Notice(L.T("没有可加入白名单的可执行文件。")); return; }
        if (!await Confirm(L.T("加入白名单"), string.Join("\n", incoming.Select(rule => rule.Name + " | " + rule.Value)))) return;
        SaveRules(WhitelistStore.MergeRules(_rules, incoming));
    }
    private async void CloseClick(object sender, RoutedEventArgs e) => await CloseTargets(_snapshot.Processes);
    private async Task CloseTargets(IReadOnlyList<ProcessRecord> candidates, bool force = false)
    {
        if (_busy || !_rulesReadable || !_backend.IsAdministrator) return;
        var snapshot = _snapshot; var frozen = candidates.Where(process => !_backend.Policy.Evaluate(process, snapshot, RunningRules).Protected).ToArray();
        if (frozen.Length == 0) { Notice(L.T("没有可关闭的进程。")); return; }
        if (!await Confirm(L.T(force ? "强制关闭" : "确认关闭"), L.T(force ? "强制结束可能丢失未保存的数据。" : "请先保存工作。") + "\n\n" + string.Join("\n", frozen.Select(p => p.ApplicationName + " | " + p.Name + " | PID " + p.Id)))) return;
        _busy = true;
        try
        {
            var closer = new ProcessCloser(capture: _backend.Capture);
            var progress = new Progress<string>(Log);
            ProtectionDecision Recheck(ProcessRecord process) => _closed || _life.IsCancellationRequested || !_rulesReadable
                ? new ProtectionDecision(true, L.T("已取消")) : _backend.Policy.Evaluate(process, _backend.Capture(), RunningRules);
            var result = _backend.ExecuteClose is not null
                ? await _backend.ExecuteClose(frozen, force, Recheck, snapshot, _life.Token)
                : await Task.Run(() => closer.CloseDetailedAsync(frozen, force, Recheck, snapshot, _life.Token, progress));
            foreach (var item in result.Results) Log(item.ProcessName + " | PID " + item.ProcessId + " | " + item.Outcome);
            Notice(L.T("关闭操作已完成，请查看操作记录。"), result.Results.All(item => item.Success));
            if (!_closed && !_life.IsCancellationRequested && result.SteamPendingForce.Count > 0 && (!RiskConfirmationMode.IsEnabled || force) &&
                (RiskConfirmationMode.IsEnabled || await Confirm(L.T("强制关闭"), L.T("强制结束可能丢失未保存的数据。") + "\n" + string.Join("\n", result.SteamPendingForce.Select(p => p.Name + " | PID " + p.Id)))))
            {
                if (_closed || _life.IsCancellationRequested || !_rulesReadable) return;
                var forced = _backend.ExecuteSteamForce is not null
                    ? await _backend.ExecuteSteamForce(result.SteamPendingForce, Recheck, _life.Token)
                    : await Task.Run(() => closer.ForceConfirmedAsync(result.SteamPendingForce, Recheck, _life.Token));
                foreach (var item in forced) Log(item.ProcessName + " | " + item.Outcome);
            }
        }
        catch (OperationCanceledException) { Notice(L.T("已取消")); } catch (Exception ex) { Notice(ex.Message); Log(ex.Message); }
        finally { _busy = false; await CaptureAsync(); await RenderAsync(); }
    }
    private async Task<bool> ShowProcessWindow(ProcessRecord process, string key)
    {
        if (_busy) return false;
        _busy = true;
        try
        {
            var candidates = process.Windows.Where(window => WindowActions.IsCandidate(process, window, true, key)).OrderByDescending(window => window.IsVisible).ThenByDescending(window => window.HasAppWindowStyle).ToArray();
            if (candidates.Length > 0)
            {
                var selected = await Pick(L.T("显示窗口"), candidates, window => window.Title + " | " + process.Name + " | PID " + process.Id);
                if (selected is null) return false;
                var result = await Task.Run(() => selected.IsVisible ? WindowActions.Activate(process, selected, key) : WindowActions.RevealHidden(process, selected, key));
                Log(result.Message); Notice(result.Message, result.Success); return result.Success;
            }
            if (AvdGuiMatcher.RequiresNativeGui(process, key))
            {
                var service = new AvdRestartService();
                var scope = _snapshot.Processes.Where(p => p.Id == process.Id || process.ParentIdentityVerified && p.Id == process.ParentId && p.OwnerSid == process.OwnerSid && p.SessionId == process.SessionId).ToArray();
                var discovery = await service.DiscoverAsync(scope, _life.Token);
                var plan = await Pick(L.T("以原生窗口重启 AVD"), discovery.Plans, p => p.AvdName + " | emulator-" + p.ConsolePort + " | " + L.T("推荐"));
                if (plan is null) { Notice(string.Join("\n", discovery.Notices)); return false; }
                if (!await Confirm(L.T("确认重启"), L.T("将退出并重新启动所选 AVD。正在运行的任务会中断，请先保存。") + "\n" + plan.AvdName + "\n" + string.Join("\n", plan.Changes))) return false;
                _busy = true; var outcome = await service.ExecuteAsync(plan, new Progress<AvdRestartProgress>(update => Notice(update.Message)), _life.Token); Log(outcome.Message); Notice(outcome.Message, outcome.Success); return outcome.Success;
            }
            var special = new SpecialWindowService(); var recovery = await special.DiscoverAsync(new[] { process }, _life.Token);
            var target = await Pick(L.T("显示窗口"), recovery.Plans, plan => plan.Name + " | " + plan.Explanation);
            if (target is null) { Notice(recovery.Notices.Count > 0 ? string.Join("\n", recovery.Notices) : L.T("没有可核实的图形界面入口。")); return false; }
            if (!await Confirm(L.T("确认打开"), target.Explanation)) return false;
            _busy = true; var opened = await special.ExecuteAsync(target, true, cancellationToken: _life.Token); Log(opened.Message); Notice(opened.Message, opened.Success); return opened.Success;
        }
        catch (Exception ex) { Notice(ex.Message); return false; }
        finally { _busy = false; }
    }
    private async Task<T?> Pick<T>(string title, IReadOnlyList<T> values, Func<T, string> name) where T : class
    {
        if (values.Count == 0) return null;
        if (values.Count == 1) return values[0];
        var list = new ListBox { ItemsSource = values.Select(name), SelectedIndex = 0, Margin = new Thickness(16), MinWidth = 360, MaxHeight = 360 };
        var window = new Window { Owner = this, Title = title, Width = 570, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Resources["PageBrush"], Foreground = (Brush)Resources["InkBrush"] };
        var panel = new StackPanel(); panel.Children.Add(list); var choose = new Button { Content = L.T("继续"), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Right }; choose.Click += (_, _) => window.DialogResult = true; panel.Children.Add(choose); window.Content = panel;
        await Task.Yield(); return window.ShowDialog() == true ? values[list.SelectedIndex] : null;
    }
    private async void AddClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var catalog = new InstalledApplicationCatalog(); var app = await Task.Run(() => catalog.FromExecutable(picker.FileName, _life.Token));
            if (app is null) { Notice(string.Join("\n", catalog.Warnings)); return; }
            if (!_installed.Any(item => item.Id == app.Id)) _installed = _installed.Concat(new[] { app }).ToArray();
            if (_page == 2) await KeepRow(new LegacyRow { Model = app }); else { UpdateDrives(); await RenderAsync(); }
        }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private async void ImportRulesClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true }; if (picker.ShowDialog(this) != true) return;
        try { var incoming = RulePortability.PrepareImport(WhitelistStore.ReadImport(picker.FileName)); if (await Confirm(L.T("导入白名单"), L.T("本机路径规则默认不启用。") + "\n" + incoming.Count)) SaveRules(WhitelistStore.MergeRules(_rules, incoming)); }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private async void ExportRulesClick(object sender, RoutedEventArgs e)
    {
        var purpose = await ChooseExportPurpose(); if (purpose is null) return;
        var picker = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "ProcessKeeper.rules.json" }; if (picker.ShowDialog(this) != true) return;
        try { WhitelistStore.ExportToFile(picker.FileName, RulesForExport(purpose.ForSharing)); Notice(L.T("导出成功"), true); } catch (Exception ex) { Notice(ex.Message); }
    }
}
