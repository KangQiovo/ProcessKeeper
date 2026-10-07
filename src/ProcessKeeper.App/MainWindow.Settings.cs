using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly ViewPreferencesStore _viewStore = new();
    private bool _viewReady, _changingViews;
    private string _languagePreference = "auto";
    public event Action? LanguageChanged;
    internal event Action? IntroductionRequested;

    private void DismissCompatibilityNotice(InfoBar sender, object args) => CompatibilityNoticeState.Dismissed = true;

    private async void OpenReferencedProject(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: ReferencedProject project } || _closed || _dialogOpen) return;
        var details = new TextBlock { Text = L.T("将在默认浏览器打开以下网址：") + "\n\n" + project.Url,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 480 };
        if (await ShowDialog(NewDialog(L.T("打开项目主页？"), details, L.T("打开浏览器"))) != ContentDialogResult.Primary || _closed) return;
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(project.Url)))
                ShowNotice(L.T("无法打开浏览器"), L.T("可复制此网址后在浏览器中打开。") + "\n" + project.Url, InfoBarSeverity.Warning);
        }
        catch (Exception) { ShowNotice(L.T("无法打开浏览器"), project.Url, InfoBarSeverity.Warning); }
    }

    private void ReopenIntroduction(object sender, RoutedEventArgs args)
    {
        if (_closed) return;
        if (HasPendingTool || _working || _dialogOpen || _checkingCompatibility || _windowOperationRunning || _autorunsView?.IsChanging == true || _updatesView?.IsBusy == true)
        {
            ShowNotice(L.T("请等待当前操作完成"), L.T("操作完成后即可重新查看引导。"), InfoBarSeverity.Informational);
            return;
        }
        IntroductionRequested?.Invoke();
    }

    internal void OpenAboutSettings()
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => Equals(item.Tag, "appearance"));
        SettingsTabs.SelectedIndex = 3;
    }

    private ViewPreferences CurrentView => new(LiveToggle.IsOn, ShowSystemToggle.IsChecked == true,
        Math.Max(0, CategoryFilter.SelectedIndex), Math.Max(0, SortFilter.SelectedIndex), _languagePreference, HistoryAutoScrollToggle.IsOn, _installedDrivePreference,
        _groupGamePlatforms, _hideMicrosoftApps, _syncEmptyProfileRules);

    private void InitializeViewPreferences(string? initialLanguagePreference = null)
    {
        try
        {
            var preferences = _viewStore.Load();
            ApplyViewPreferences(initialLanguagePreference is null ? preferences : preferences with { Language = initialLanguagePreference });
        }
        catch (Exception ex)
        {
            ApplyViewPreferences(new ViewPreferences(Language: initialLanguagePreference ?? "auto"));
            ShowNotice(L.T("列表设置已使用默认值"), ex.Message, InfoBarSeverity.Warning);
        }
        _viewReady = true;
    }

    private void ApplyViewPreferences(ViewPreferences value)
    {
        _changingViews = true;
        try
        {
            _languagePreference = value.Language;
            _syncEmptyProfileRules = value.SyncEmptyProfileRules;
            LanguageChoice.SelectedIndex = Array.IndexOf(new[] { "auto", "zh-Hans", "zh-Hant", "en" }, value.Language);
            LanguageStatus.Text = L.F($"当前界面语言：{L.LanguageName(LanguageResolver.ResolveCurrent(value.Language))}");
            LiveToggle.IsOn = value.LiveRefresh;
            ShowSystemToggle.IsChecked = value.ShowSystemProcesses;
            SystemCategoryOption.IsEnabled = value.ShowSystemProcesses;
            CategoryFilter.SelectedIndex = value.CategoryFilter;
            SortFilter.SelectedIndex = value.SortOrder;
            HistoryAutoScrollToggle.IsOn = value.HistoryAutoScroll;
            ApplyPresentationPreferences(value.GroupGamePlatforms, value.HideMicrosoftApps);
            ApplyInstalledDrivePreference(value.InstalledDrive);
        }
        finally { _changingViews = false; }
        QueueHistoryScroll();
        if (_ready) { RenderApps(); RenderInstalled(); RequestPresentationCapture(); }
    }

    private void LiveRefreshChanged(object sender, RoutedEventArgs args) => SaveViewPreferences();

    private void LanguageSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_viewReady || _changingViews || _closed || LanguageChoice.SelectedIndex < 0) return;
        var selected = new[] { "auto", "zh-Hans", "zh-Hant", "en" }[LanguageChoice.SelectedIndex];
        if (selected == _languagePreference) return;
        if (HasPendingTool || _working || _dialogOpen || _windowOperationRunning || _uninstallView?.IsBusy == true || _autorunsView?.IsChanging == true || _updatesView?.IsBusy == true)
        {
            _changingViews = true;
            try { LanguageChoice.SelectedIndex = Array.IndexOf(new[] { "auto", "zh-Hans", "zh-Hant", "en" }, _languagePreference); }
            finally { _changingViews = false; }
            ShowNotice(L.T("请等待当前操作完成"), "", InfoBarSeverity.Informational);
            return;
        }
        try
        {
            _viewStore.Save(CurrentView with { Language = selected });
            _languagePreference = selected;
            L.Language = LanguageResolver.ResolveCurrent(selected);
            LanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ApplyViewPreferences(CurrentView);
            ShowNotice(L.T("语言设置未保存"), ex.Message, InfoBarSeverity.Warning);
        }
    }

    internal void OpenLanguageSettings()
    {
        Navigation.SelectedItem = Navigation.MenuItems.OfType<NavigationViewItem>().First(item => item.Tag as string == "appearance");
        SettingsTabs.SelectedIndex = 0;
    }
    private void SaveViewPreferences()
    {
        if (!_viewReady || _changingViews || _closed || _working) return;
        try { _viewStore.Save(CurrentView); }
        catch (Exception ex) { ShowNotice(L.T("列表设置未保存"), ex.Message, InfoBarSeverity.Warning); }
    }

    private async void ClearAppCache(object sender, RoutedEventArgs args)
    {
        if (_closed || _dialogOpen || _working || HasPendingTool || _windowOperationRunning || _autorunsView?.IsChanging == true || _uninstallView?.IsBusy == true || _updatesView?.IsBusy == true) return;
        if (!LauncherContextReader.TryGetCurrent(out var context, out var error))
        { ShowNotice(L.T("缓存清理未完成"), error, InfoBarSeverity.Warning); return; }
        if (await ShowDialog(NewDialog(L.T("清理应用缓存？"), new TextBlock
            { Text = L.T("清理旧版本缓存、遗留更新包与未完成下载；使用中的文件、白名单和设置会保留。"), TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, L.T("清理缓存"))) != ContentDialogResult.Primary || _closed) return;
        if (_working || HasPendingTool || _windowOperationRunning || _autorunsView?.IsChanging == true || _uninstallView?.IsBusy == true || _updatesView?.IsBusy == true) return;
        _working = true; ClearCacheButton.IsEnabled = false; ClearCacheButton.Content = L.T("正在清理…");
        try
        {
            var result = await Task.Run(() => AppCacheCleanupService.Clear(context!));
            if (!_closed) ShowNotice(L.T(result.Success ? "缓存清理完成" : "缓存清理未完成"), result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally
        { _working = false; if (!_closed) { ClearCacheButton.IsEnabled = true; ClearCacheButton.Content = L.T("清理应用缓存"); } }
    }

    private void OpenConfigurationFolder(object sender, RoutedEventArgs args)
    {
        try
        {
            var directory = Path.GetDirectoryName(_store.FilePath)!;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        }
        catch (Exception ex) { ShowNotice(L.T("无法打开配置目录"), ex.Message, InfoBarSeverity.Warning); }
    }

    private void SettingsDiagnosticsLoaded(object sender, RoutedEventArgs args) => UpdateSettingsDiagnostics();
    private void RefreshSettingsDiagnostics(object sender, RoutedEventArgs args) => UpdateSettingsDiagnostics();

    private void CopySettingsDiagnostics(object sender, RoutedEventArgs args)
    {
        UpdateSettingsDiagnostics();
        CopyText(SettingsDiagnostics.Text, L.T("诊断信息已复制"));
    }

    private void UpdateSettingsDiagnostics()
    {
        if (!_ready || _closed || SettingsDiagnostics is null) return;
        try
        {
            using var current = Process.GetCurrentProcess();
            var version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? L.T("未知");
            var captured = _snapshot.CapturedAt == DateTimeOffset.MinValue ? L.T("尚未采集") :
                _snapshot.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SettingsDiagnostics.Text = L.F($"Process Keeper {version}\n") +
                $"Windows：{RuntimeInformation.OSDescription}\n" +
                L.F($"运行环境：{RuntimeInformation.FrameworkDescription} | {RuntimeInformation.ProcessArchitecture}\n") +
                L.F($"应用内存：{Memory(current.WorkingSet64)}\n") +
                L.F($"当前快照：{_snapshot.Applications.Count} 个程序 | {_snapshot.Processes.Count} 个进程\n") +
                L.F($"采集时间：{captured}\n") +
                L.F($"进程自动刷新：{(LiveToggle.IsOn ? L.T("每 2 秒") : L.T("已暂停"))}\n") +
                L.F($"配置状态：{(_configurationHealthy ? L.T("可用") : L.T("需要恢复配置"))}");
        }
        catch (Exception ex)
        {
            SettingsDiagnostics.Text = L.T("部分诊断信息暂时无法读取。");
            ShowNotice(L.T("诊断信息读取失败"), ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void ExportAllSettings(object sender, RoutedEventArgs args)
    {
        if (_dialogOpen || _working || !_configurationHealthy || _updatesView?.IsBusy == true) return;
        var portable = RulePortability.ForSharing(_rules);
        var scope = new ComboBox
        {
            Header = L.T("导出内容"), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = new[] { L.T("全部设置与白名单"), L.T("仅白名单") }
        };
        var choice = new ComboBox
        {
            Header = L.T("导出用途"), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = new[] { L.T("分享给其他电脑（仅通用程序身份）"), L.T("完整备份（包含本机路径与名称规则）") }
        };
        var panel = new StackPanel { Spacing = 14, MaxWidth = 500, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(scope);
        panel.Children.Add(choice);
        var description = new TextBlock { TextWrapping = TextWrapping.Wrap };
        void UpdateExportDescription() => description.Text =
            (scope.SelectedIndex == 1 ? L.T("仅包含白名单规则，不包含语言、外观或其他设置。") : L.T("包含白名单、外观与列表设置。")) + "\n\n" +
            L.F($"当前配置：分享 {portable.Count} 条通用规则；完整备份 {_rules.Count} 条规则。") +
            (scope.SelectedIndex == 1 ? "" : "\n" + L.T("全部设置包含所有白名单配置。"));
        scope.SelectionChanged += (_, _) => UpdateExportDescription();
        UpdateExportDescription();
        panel.Children.Add(description);
        var viewport = new ScrollViewer
        {
            Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        // ContentDialog's native presenter can clip tall content in short windows
        // without exposing a scroll range. Give the options their own bounded viewport.
        void FitExportViewport() => viewport.MaxHeight = Math.Clamp(Root.ActualHeight - 220, 80, 420);
        FitExportViewport();
        SizeChangedEventHandler resized = (_, _) => FitExportViewport();
        Root.SizeChanged += resized;
        ContentDialogResult decision;
        try { decision = await ShowDialog(NewDialog(L.T("导出"), viewport, L.T("选择保存位置"))); }
        finally { Root.SizeChanged -= resized; }
        if (decision != ContentDialogResult.Primary || _closed) return;
        var rulesOnly = scope.SelectedIndex == 1;
        var exportedRules = choice.SelectedIndex == 0 ? portable : _rules;
        _dialogOpen = true;
        try
        {
            // Reading the persisted settings here rejects corruption instead of exporting the view's display fallback.
            var profileExport = rulesOnly ? null : new WhitelistProfilesStore(Path.GetDirectoryName(_store.FilePath)!).ExportSnapshot();
            if (profileExport is not null && choice.SelectedIndex == 0)
                profileExport = profileExport with { Profiles = profileExport.Profiles.Select(profile => profile with { Rules = RulePortability.ForSharing(profile.Rules) }).ToArray() };
            var bundle = profileExport is null ? null : new SettingsBundle(profileExport.ActiveRules, CurrentAppearance, CurrentView, new UpdatePreferencesStore().Load(), profileExport, PerformanceForExport(), WhitelistScopeForExport());
            var picker = new FileSavePicker(AppWindow.Id)
            {
                SuggestedFileName = $"ProcessKeeper-{(rulesOnly ? choice.SelectedIndex == 0 ? L.T("分享规则") : L.T("完整备份规则") : choice.SelectedIndex == 0 ? L.T("分享设置") : L.T("全部设置备份"))}-{DateTime.Now:yyyyMMdd}",
                DefaultFileExtension = ".json"
            };
            picker.FileTypeChoices.Add(rulesOnly ? L.T("白名单规则 JSON") : L.T("全部设置 JSON"), new List<string> { ".json" });
            var file = await picker.PickSaveFileAsync();
            if (file is null || _closed) return;
            if (rulesOnly) WhitelistStore.ExportToFile(file.Path, exportedRules);
            else SettingsBundleStore.Export(file.Path, bundle!);
            Log(rulesOnly ? L.F($"已导出 {exportedRules.Count} 条{(choice.SelectedIndex == 0 ? L.T("分享规则") : L.T("完整备份规则"))}：{file.Path}") : L.T("已导出全部设置：") + file.Path);
            ShowNotice(rulesOnly ? L.T("规则已导出") : L.T("全部设置已导出"), file.Path, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowNotice(L.T("设置导出失败"), ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
    }

    private async void ImportAllSettings(object sender, RoutedEventArgs args)
    {
        if (_dialogOpen || _working || !_configurationHealthy || _updatesView?.IsBusy == true) return;
        SettingsBundle? imported = null;
        var source = "";
        _dialogOpen = true;
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".json");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            imported = SettingsBundleStore.ReadImport(file.Path);
            source = Path.GetFileName(file.Path);
        }
        catch (Exception ex) { ShowNotice(L.T("未导入设置"), ex.Message, InfoBarSeverity.Error); }
        finally { _dialogOpen = false; }
        if (imported is null) return;

        var localCount = imported.Profiles is { } importedProfiles
            ? importedProfiles.Profiles.Sum(profile => profile.Rules.Count(rule => !RulePortability.IsPortable(rule)))
            : imported.Rules.Count(r => !RulePortability.IsPortable(r));
        var ruleMode = new ComboBox
        {
            Header = L.T("白名单处理方式"), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0,
            ItemsSource = new[] { L.T("合并到现有白名单（保留已有设置）"), L.T("替换现有白名单") }
        };
        var allowLocal = new CheckBox
        {
            Content = L.F($"我已核对，按文件设置启用 {localCount} 条本机规则"), IsChecked = false,
            Visibility = localCount > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var themes = new[] { L.T("跟随系统"), L.T("浅色"), L.T("深色") };
        var sorts = new[] { L.T("默认顺序"), L.T("名称"), L.T("内存占用"), L.T("进程数量") };
        var categories = new[] { L.T("全部运行状态"), L.T("有可见窗口"), L.T("已最小化"), L.T("隐藏后台"), L.T("服务与系统") };
        var material = imported.Appearance.BackdropEnabled ? ((int)imported.Appearance.Material == 0 ? L.T("云母 Mica") : L.T("亚克力磨砂 Acrylic")) : L.T("纯色背景");
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock
        {
            Text = L.F($"{source}\n\n背景：{material}　主题：{themes[(int)imported.Appearance.Theme]}\n排序：{sorts[imported.View.SortOrder]}　筛选：{categories[imported.View.CategoryFilter]}\n系统与服务：{(imported.View.ShowSystemProcesses ? L.T("显示") : L.T("隐藏"))}　实时刷新：{(imported.View.LiveRefresh ? L.T("开启") : L.T("暂停"))}\n\n白名单 {imported.Rules.Count} 条，本机规则 {localCount} 条默认停用。现有配置会先备份。"),
            TextWrapping = TextWrapping.Wrap
        });
        var preview = new TextBox { IsReadOnly = true, AcceptsReturn = true, MaxHeight = 150, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        void UpdatePreview() => preview.Text = string.Join("\n", RulePortability.PrepareImport(imported.Rules, allowLocal.IsChecked == true)
            .Select(r => $"[{(r.Enabled ? L.T("启用") : L.T("停用"))}] {WhitelistStore.GetDisplayName(r)} | {r.Value}"));
        allowLocal.Click += (_, _) => UpdatePreview();
        UpdatePreview();
        panel.Children.Add(preview);
        if (imported.Profiles is not null)
            panel.Children.Add(new TextBlock { Text = L.F($"包含 {imported.Profiles.Profiles.Count} 套配置；将整体恢复配置列表。"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(ruleMode);
        panel.Children.Add(allowLocal);
        panel.Children.Add(new TextBlock { Text = L.T("确认后立即应用界面设置。规则导入只更新白名单，不触发关闭操作。"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        panel.Children.Add(new TextBlock { Text = L.F($"界面语言：{(imported.View.Language == "auto" ? L.T("自动（系统语言优先）") : L.LanguageName(imported.View.Language))}"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.F($"操作记录自动滚动：{(imported.View.HistoryAutoScroll ? L.T("开启") : L.T("暂停"))}"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = L.F($"已安装应用盘符：{(imported.View.InstalledDrive.Length == 0 ? L.T("所有盘符") : imported.View.InstalledDrive)}"), TextWrapping = TextWrapping.Wrap });
        if (imported.Updates is { } updates)
            panel.Children.Add(new TextBlock { Text = L.F($"更新仓库：{updates.Repository}"), TextWrapping = TextWrapping.Wrap });
        var importViewport = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void FitImportViewport() => importViewport.MaxHeight = Math.Clamp(Root.ActualHeight - 220, 80, 420);
        FitImportViewport();
        SizeChangedEventHandler resized = (_, _) => FitImportViewport(); Root.SizeChanged += resized;
        ContentDialogResult importDecision;
        try { importDecision = await ShowDialog(NewDialog(L.T("预览全部设置"), importViewport, L.T("确认导入"))); }
        finally { Root.SizeChanged -= resized; }
        if (importDecision != ContentDialogResult.Primary || _closed) return;
        if (HasPendingTool || _working || _dialogOpen || !_configurationHealthy || _windowOperationRunning || _updatesView?.IsBusy == true)
        { ShowNotice(L.T("请等待当前操作完成"), "", InfoBarSeverity.Informational); return; }
        try
        {
            var prepared = RulePortability.PrepareImport(imported.Rules, allowLocal.IsChecked == true);
            var rules = ruleMode.SelectedIndex == 1 ? prepared : WhitelistStore.MergeRules(_rules, prepared);
            var profiles = imported.Profiles;
            if (profiles is not null)
                profiles = profiles with { Profiles = profiles.Profiles.Select(profile => profile with
                { Rules = profile.Id == profiles.ActiveId ? rules : RulePortability.PrepareImport(profile.Rules, allowLocal.IsChecked == true) }).ToArray() };
            var next = new SettingsBundle(rules, imported.Appearance, imported.View, imported.Updates, profiles, imported.Performance, imported.WhitelistScope);
            await FlushPerformanceAsync();
            SettingsBundleStore.CommitAll(Path.GetDirectoryName(_store.FilePath)!, next);
            var previousLanguage = L.Language;
            _rules = rules;
            ApplyWhitelistScope(next.WhitelistScope);
            _configurationHealthy = _scopeReadable;
            RefreshWhitelistActionGuards();
            ApplyImportedAppearance(next.Appearance);
            ApplyViewPreferences(next.View);
            ProfilesHost.Content = null;
            EnsureProfilesView();
            if (next.Updates is not null) _updatesView?.Apply(next.Updates);
            if (next.Performance is not null) _performanceView?.ApplyPreferences(next.Performance);
            _lastDetailSignature = "";
            UpdateRules();
            RenderApps();
            Log(L.T("已导入全部设置：") + source);
            ShowNotice(L.T("全部设置已应用"), L.T("白名单、外观、列表与更新设置已更新，原配置已备份。"), InfoBarSeverity.Success);
            L.Language = LanguageResolver.ResolveCurrent(next.View.Language);
            if (L.Language != previousLanguage) LanguageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            if (ex is SettingsCommitException { RollbackSucceeded: false })
            {
                _configurationHealthy = false;
                UpdateRules();
                RenderApps();
            }
            ShowNotice(L.T("全部设置未能完整导入"), ex.Message, InfoBarSeverity.Error);
        }
    }
}
