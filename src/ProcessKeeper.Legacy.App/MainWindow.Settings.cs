using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public partial class MainWindow
{
    private string _settingsLanguage = "";
    private static TextBlock Text(string value, double size = 14) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
    private static Button Button(string value, Action action) { var button = new Wpf.Ui.Controls.Button { Content = value, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 12), Padding = new Thickness(12, 8, 12, 8) }; button.Click += (_, _) => action(); return button; }
    private void ApplyTheme()
    {
        var dark = _appearance.Theme == AppearanceTheme.Dark;
        if (_appearance.Theme == AppearanceTheme.System)
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int value && value == 0; } catch { dark = false; }
        }
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
        Resources["PageBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#202124" : "#F4F5F7"));
        Resources["PanelBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#292B30" : "#FFFFFF"));
        Resources["InkBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#F3F4F6" : "#17191C"));
        Resources["MutedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#BFC5D0" : "#505763"));
        Resources["RoleSuccessBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#153E25" : "#DFF6DD"));
        Resources["RoleCautionBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#433519" : "#FFF4CE"));
        Resources["RoleCriticalBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#442726" : "#FDE7E9"));
        // Popup menus are rebuilt after changes so a previously open menu cannot keep the old theme.
        List.ContextMenu = new ContextMenu();
    }
    private void BuildSettings()
    {
        _settingsLanguage = L.Language;
        SettingsContent.Children.Clear();
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        SettingsContent.Children.Add(tabs);
        StackPanel Tab(string title) { var panel = new StackPanel { Margin = new Thickness(12, 18, 12, 12), HorizontalAlignment = HorizontalAlignment.Stretch }; tabs.Items.Add(new TabItem { Header = L.T(title), Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top }); return panel; }
        var language = Tab("语言");
        language.Children.Add(Text(L.T("显示语言"), 20));
        var languages = new ComboBox { ItemsSource = new[] { L.T("自动（跟随系统）"), "简体中文", "繁體中文", "English" }, SelectedIndex = Array.IndexOf(new[] { "auto", "zh-Hans", "zh-Hant", "en" }, _view.Language), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left };
        bool restoringLanguage = false;
        languages.SelectionChanged += async (_, _) =>
        {
            if (restoringLanguage || languages.SelectedIndex < 0) return;
            var codes = new[] { "auto", "zh-Hans", "zh-Hant", "en" };
            if (codes[languages.SelectedIndex] == _view.Language) return;
            if (HasPendingTool || _closed || _busy || _uninstallView?.IsBusy == true || _updateDialogOpen || _updateDownloading || _shortcutBusy)
            {
                restoringLanguage = true;
                try { languages.SelectedIndex = Array.IndexOf(codes, _view.Language); }
                finally { restoringLanguage = false; }
                if (!_closed) Notice(L.T("请等待当前操作完成"));
                return;
            }
            _view = _view with { Language = codes[languages.SelectedIndex] };
            SaveView(); L.Language = LanguageResolver.ResolveCurrent(_view.Language); ConfigureLanguage(); await RenderAsync();
        };
        language.Children.Add(languages);
        var appearance = Tab("外观"); BuildCompatibilityAppearance(appearance);
        var backup = Tab("备份"); backup.Children.Add(Text(L.T("全部设置"), 20));
        backup.Children.Add(Button(L.T("导出"), ExportSettings)); backup.Children.Add(Button(L.T("导入全部设置"), async () => await ImportSettings()));
        backup.Children.Add(Button(L.T("导出白名单"), () => ExportRulesClick(this, new RoutedEventArgs())));
        backup.Children.Add(Button(L.T("导入白名单"), () => ImportRulesClick(this, new RoutedEventArgs())));
        AddProfilesView(backup);
        var about = Tab("关于"); about.Children.Add(Text("Process Keeper", 24)); about.Children.Add(BuildAboutVersion()); AddAboutBuildHash(about);
        if (BuildInfo.IsPreviewBuild) about.Children.Add(Text(L.T("测试版本不代表最终品质"))); BuildUpdateSettings(about); about.Children.Add(Text(L.T("作者信息"), 20)); AddAuthorAvatar(about); about.Children.Add(Text("KangQi"));
        var links = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left }; links.Children.Add(AuthorButton("GitHub", AuthorIcons.Github, "https://github.com/KangQiovo")); links.Children.Add(AuthorButton(L.T("酷安"), AuthorIcons.Coolapk, "https://www.coolapk.com/u/21241695", true)); links.Children.Add(AuthorButton(L.T("B站"), AuthorIcons.Bilibili, "https://space.bilibili.com/329073257")); about.Children.Add(links);
        var projectHome = Button("Process Keeper | " + L.T("项目主页"), async () =>
        { const string url = "https://github.com/KangQiovo/ProcessKeeper"; if (await Confirm(L.T("打开项目主页？"), L.T("将在默认浏览器打开以下网址：") + "\n" + url)) OpenWebsite(url); });
        projectHome.Content = new TextBlock { Text = "Process Keeper | " + L.T("项目主页"), TextWrapping = TextWrapping.Wrap };
        projectHome.Tag = "https://github.com/KangQiovo/ProcessKeeper"; projectHome.HorizontalAlignment = HorizontalAlignment.Stretch; projectHome.HorizontalContentAlignment = HorizontalAlignment.Left;
        projectHome.BorderThickness = new Thickness(0); projectHome.Background = Brushes.Transparent; about.Children.Add(Text(L.T("本项目"), 20)); about.Children.Add(projectHome);
        about.Children.Add(Text(L.T("引用项目"), 20));
        foreach (var project in ReferencedProjects.All)
        {
            var reference = Button(project.Name, async () => { if (await Confirm(L.T("打开项目主页？"), L.T("将在默认浏览器打开以下网址：") + "\n" + project.Url)) OpenWebsite(project.Url); });
            reference.Content = new TextBlock { Text = project.Name, TextWrapping = TextWrapping.Wrap };
            reference.Tag = project.Url; reference.HorizontalAlignment = HorizontalAlignment.Stretch; reference.HorizontalContentAlignment = HorizontalAlignment.Left;
            reference.BorderThickness = new Thickness(0); reference.Background = Brushes.Transparent;
            about.Children.Add(reference);
            about.Children.Add(Text(project.Description, 12));
        }
        about.Children.Add(Button(L.T("运行环境检测"), async () => await CheckCompatibilityAsync()));
        about.Children.Add(Button(L.T("重新进入引导（OOBE）"), () => { if (HasPendingTool || _busy || _checkingCompatibility) Notice(L.T("请等待当前操作完成")); else ShowOnboarding(true); }));
        about.Children.Add(Text(L.T("本机文件"), 20));
        about.Children.Add(Button(L.T("清理应用缓存"), async () => await ClearAppCacheAsync()));
        about.Children.Add(Button(L.T("打开配置与记录目录"), () =>
        { try { Process.Start(new ProcessStartInfo(_directory) { UseShellExecute = true }); } catch (Exception ex) { Notice(ex.Message); } }));
        about.Children.Add(Text(L.T("清理旧版本缓存、遗留更新包与未完成下载；使用中的文件、白名单和设置会保留。"), 12));
        var diagnostics = Text(L.T("正在读取运行环境…")); about.Children.Add(diagnostics);
        about.Children.Add(Button(L.T("刷新诊断信息"), async () => await LoadDiagnostics()));
        _ = LoadDiagnostics();
        async Task LoadDiagnostics()
        {
            try
            {
                var result = await Task.Run(_backend.CaptureEnvironment, _life.Token);
                if (!_closed) diagnostics.Text = result.Description;
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception ex)
            {
                if (_closed) return;
                diagnostics.Text = L.T("部分运行环境无法读取，可重新检测。");
                Notice(L.T("环境检测需要关注") + " | " + ex.Message);
            }
        }

        var risk = Button("", async () => await ToggleRiskModeAsync());
        risk.Tag = "risk-mode-toggle"; risk.Background = new SolidColorBrush(Color.FromRgb(176, 0, 32)); risk.Foreground = Brushes.White;
        risk.Style = (Style)Resources[typeof(Button)]; risk.Content = new TextBlock { Text = L.T(RiskConfirmationMode.IsEnabled ? "恢复风险确认" : "无视风险模式"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
        about.Children.Add(Text(L.T("仅本次运行生效；重新启动后恢复风险确认。"))); about.Children.Add(risk);
    }
    private void BuildCompatibilityAppearance(StackPanel appearance)
    {
        appearance.Children.Add(Text(L.T("应用主题"), 20));
        var themes = new ComboBox { Tag = "appearance-theme", ItemsSource = new[] { L.T("跟随系统"), L.T("浅色"), L.T("深色") }, SelectedIndex = (int)_appearance.Theme, MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) };
        appearance.Children.Add(themes); appearance.Children.Add(Text(L.T("当前环境使用纯色背景。")));
        var explanation = Text(L.T("此兼容界面不支持原生材质参数，设置保留供现代界面使用。"), 12);
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); appearance.Children.Add(explanation);
        var parameters = new StackPanel { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var region = new Expander { Tag = "appearance-native-parameters", Header = new TextBlock { Text = L.T("自定义材质参数"), TextWrapping = TextWrapping.Wrap }, Content = parameters,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, IsExpanded = true, Margin = new Thickness(0, 0, 0, 16) };
        region.SetResourceReference(Control.ForegroundProperty, "InkBrush"); appearance.Children.Add(region);
        var custom = new CheckBox { Tag = "appearance-custom-enabled", Content = new TextBlock { Text = L.T("启用自定义材质参数"), TextWrapping = TextWrapping.Wrap }, IsEnabled = false, Margin = new Thickness(0, 0, 0, 12) };
        custom.SetResourceReference(Control.ForegroundProperty, "InkBrush"); parameters.Children.Add(custom);
        var tintLabel = Text("", 12); parameters.Children.Add(tintLabel);
        var tint = new Slider { Tag = "appearance-tint-opacity", Minimum = 0, Maximum = 1, IsEnabled = false, Margin = new Thickness(0, 0, 0, 16) }; parameters.Children.Add(tint);
        var luminosityLabel = Text("", 12); parameters.Children.Add(luminosityLabel);
        var luminosity = new Slider { Tag = "appearance-luminosity-opacity", Minimum = 0, Maximum = 1, IsEnabled = false, Margin = new Thickness(0, 0, 0, 16) }; parameters.Children.Add(luminosity);
        parameters.Children.Add(Text(L.T("色调颜色"), 12));
        var color = new TextBox { Tag = "appearance-tint-color", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 0, Margin = new Thickness(0, 0, 0, 12) };
        color.SetResourceReference(Control.ForegroundProperty, "InkBrush"); parameters.Children.Add(color);
        var resetNative = Button(L.T("恢复原生默认"), () => { }); resetNative.Tag = "appearance-reset-native"; resetNative.IsEnabled = false; parameters.Children.Add(resetNative);
        bool refreshing = false;
        void Refresh()
        {
            refreshing = true;
            try
            {
                themes.SelectedIndex = (int)_appearance.Theme; custom.IsChecked = _appearance.CustomBackdropEnabled;
                tint.Value = _appearance.TintOpacity; luminosity.Value = _appearance.LuminosityOpacity;
                tintLabel.Text = L.T("色调不透明度") + " | " + (_appearance.TintOpacity * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
                luminosityLabel.Text = L.T("亮度层不透明度") + " | " + (_appearance.LuminosityOpacity * 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
                color.Text = _appearance.TintColor ?? L.T("跟随主题");
            }
            finally { refreshing = false; }
        }
        void Save()
        { try { new AppearancePreferencesStore(_directory).Save(_appearance); ApplyTheme(); Refresh(); } catch (Exception ex) { Notice(ex.Message); } }
        themes.SelectionChanged += (_, _) => { if (refreshing || themes.SelectedIndex < 0) return; _appearance = _appearance with { Theme = (AppearanceTheme)themes.SelectedIndex }; Save(); };
        var reset = Button(L.T("恢复默认外观"), () => { _appearance = new AppearancePreferences(); Save(); }); reset.Tag = "appearance-reset"; appearance.Children.Add(reset);
        Refresh();
    }
    private async Task ClearAppCacheAsync()
    {
        if (_closed || _busy || HasPendingTool || _updateDownloading || _updateDialogOpen) return;
        if (!LauncherContextReader.TryGetCurrent(out var context, out var error)) { Notice(L.T("缓存清理未完成") + " | " + error); return; }
        if (!await Confirm(L.T("清理应用缓存？"), L.T("清理旧版本缓存、遗留更新包与未完成下载；使用中的文件、白名单和设置会保留。")) || _closed) return;
        if (_busy || HasPendingTool || _updateDownloading) return;
        _busy = true;
        try { var result = await Task.Run(() => AppCacheCleanupService.Clear(context!)); if (!_closed) Notice(L.T(result.Success ? "缓存清理完成" : "缓存清理未完成") + " | " + result.Message); }
        finally { _busy = false; }
    }
    private void OpenWebsite(string uri) { try { if (_backend.OpenWebPage is not null) _backend.OpenWebPage(uri); else Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { Notice(ex.Message); } }
    private Button AuthorButton(string name, string geometry, string uri, bool stroked = false)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry) };
        path.SetResourceReference(stroked ? System.Windows.Shapes.Shape.StrokeProperty : System.Windows.Shapes.Shape.FillProperty, "InkBrush");
        if (stroked) { path.StrokeThickness = 12; path.StrokeLineJoin = PenLineJoin.Round; path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round; }
        var canvas = new Canvas { Width = stroked ? 192 : 24, Height = stroked ? 192 : 24 }; canvas.Children.Add(path);
        var panel = new StackPanel { Orientation = Orientation.Horizontal }; panel.Children.Add(new Viewbox { Child = canvas, Width = 20, Height = 20, Margin = new Thickness(0, 0, 8, 0) }); panel.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        var button = Button(name, () => OpenWebsite(uri)); button.Content = panel; return button;
    }
    private sealed record ExportPurpose(bool ForSharing, string Label);
    private async Task<ExportPurpose?> ChooseExportPurpose() => await Pick(L.T("导出用途"), new[]
    {
        new ExportPurpose(true, L.T("分享给其他电脑（仅通用规则）")),
        new ExportPurpose(false, L.T("完整本机备份（保留所有规则与路径）"))
    }, purpose => purpose.Label);
    private IReadOnlyList<WhitelistRule> RulesForExport(bool forSharing) => forSharing ? RulePortability.ForSharing(_rules) : _rules;
    private async void ExportSettings() => await ExportSettingsAsync();
    private async Task ExportSettingsAsync()
    {
        var options = await ChooseExportOptions(); if (options is null) return;
        var filename = options.WhitelistOnly ? "ProcessKeeper.rules.json" : "ProcessKeeper.settings.json";
        string? path;
        if (_backend.SelectExportPath is not null) path = _backend.SelectExportPath(filename);
        else { var picker = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = filename }; path = picker.ShowDialog(this) == true ? picker.FileName : null; }
        if (path is null || string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var rules = RulesForExport(options.ForSharing);
            if (options.WhitelistOnly) WhitelistStore.ExportToFile(path, rules);
            else
            {
                var profiles = new WhitelistProfilesStore(_directory).ExportSnapshot();
                if (options.ForSharing) profiles = profiles with { Profiles = profiles.Profiles.Select(profile => profile with { Rules = RulePortability.ForSharing(profile.Rules) }).ToArray() };
                SettingsBundleStore.Export(path, new SettingsBundle(profiles.ActiveRules, _appearance, _view, ReadUpdatesForExport(), profiles, PerformanceForExport(), WhitelistScopeForExport()));
            }
            Notice(L.T("导出成功"), true);
        }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private UpdatePreferences ReadUpdatesForExport() => new UpdatePreferencesStore(_directory).Load();
    private async Task ImportSettings()
    {
        if (_closed || _busy || HasPendingTool) return;
        var picker = new OpenFileDialog { Filter = "JSON (*.json)|*.json", CheckFileExists = true }; if (picker.ShowDialog(this) != true) return;
        try
        {
            var bundle = SettingsBundleStore.ReadImport(picker.FileName);
            var profiles = bundle.Profiles;
            if (profiles is not null) profiles = profiles with { Profiles = profiles.Profiles.Select(profile => profile with { Rules = RulePortability.PrepareImport(profile.Rules) }).ToArray() };
            bundle = bundle with { Rules = profiles?.ActiveRules ?? RulePortability.PrepareImport(bundle.Rules), Profiles = profiles };
            var detail = L.T("将替换当前设置和白名单，并保留备份。");
            if (profiles is not null) detail += "\n" + L.F($"包含 {profiles.Profiles.Count} 套配置；将整体恢复配置列表。");
            if (!await Confirm(L.T("导入全部设置"), detail)) return;
            if (_closed || _busy) return;
            await FlushPerformanceAsync();
            SettingsBundleStore.CommitAll(_directory, bundle); ApplyImportedSettings(bundle); Log(L.T("全部设置已导入")); await RenderAsync();
        }
        catch (SettingsCommitException ex) { if (!ex.RollbackSucceeded) _rulesReadable = false; Notice(ex.Message); }
        catch (Exception ex) { Notice(ex.Message); }
    }
    private void ApplyImportedSettings(SettingsBundle bundle)
    {
        var ready = _ready; _ready = false;
        try
        {
            ApplyWhitelistScope(bundle.WhitelistScope);
            _rules = bundle.Rules; _view = bundle.View; _appearance = bundle.Appearance; _rulesReadable = _scopeReadable;
            RefreshWhitelistActionGuards();
            if (bundle.Updates is not null) ApplyUpdatePreferences(bundle.Updates);
            _livePages[0] = _livePages[1] = _livePages[2] = _view.LiveRefresh;
            L.Language = LanguageResolver.ResolveCurrent(_view.Language);
            SettingsContent.Children.Clear();
            ApplyTheme(); ConfigureLanguage();
            if (bundle.Performance is not null) _performanceView?.ApplyPreferences(bundle.Performance);
            ShowSystem.IsChecked = _view.ShowSystemProcesses; HistoryAuto.IsChecked = _view.HistoryAutoScroll;
        }
        finally { _ready = ready; }
        RequestPresentationCapture();
    }
}
