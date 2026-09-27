using ProcessKeeper.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using System.Runtime.InteropServices;

namespace ProcessKeeper.App;

/// <summary>Shows first-run help or the access gate without creating any process collector.</summary>
public sealed partial class StartupWindow : Window
{
    private readonly bool _administrator;
    private readonly bool _canElevate;
    private readonly OnboardingStore _store;
    private readonly ViewPreferencesStore _viewStore;
    private readonly Func<CancellationToken, Task<ElevationRequestResult>> _elevate;
    private readonly OnboardingProgress _progress = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _closed, _saveFailed;
    private bool _renderingLanguage, _languageReady;
    private string _languagePreference = "auto";
    private readonly bool _review;
    private bool _checkingEnvironment;
    private RuntimeEnvironmentReport? _environmentReport;
    private string? _environmentError;
    private bool _welcomeReady, _transitioning, _environmentBlocking;
    private Storyboard? _welcomeTransition;
    internal Func<RuntimeEnvironmentReport> ProbeEnvironment { get; set; } = RuntimeEnvironmentProbe.Capture;

    internal event Action? IntroductionCompleted;
    internal string LanguagePreference => _languagePreference;

    internal StartupWindow(bool administrator, OnboardingStore store, bool canElevate,
        Func<CancellationToken, Task<ElevationRequestResult>> elevate, string? warning = null,
        ViewPreferencesStore? viewStore = null, bool review = false, ElementTheme theme = ElementTheme.Default,
        bool? compatibilityEnvironment = null)
    {
        InitializeComponent();
        RiskModeNotice.Attach(this, RiskNotice);
        CompatibilityNotice.IsOpen = !CompatibilityNoticeState.Dismissed &&
            (compatibilityEnvironment ?? CompatibilityNoticeState.ShouldShow());
        _administrator = administrator;
        _review = review;
        StartupRoot.RequestedTheme = theme;
        _store = store;
        _viewStore = viewStore ?? new ViewPreferencesStore();
        _canElevate = canElevate;
        _elevate = elevate;
        if (administrator)
        {
            try { _languagePreference = _viewStore.Load().Language; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(StartupTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProcessKeeper.ico"));
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var margin = (int)Math.Round(48 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min((int)Math.Round(1120 * scale), Math.Max(320, workArea.Width - margin)),
            Math.Min((int)Math.Round(820 * scale), Math.Max(320, workArea.Height - margin))));
        WindowSizePolicy.Attach(this);
        StartupRoot.SizeChanged += (_, _) => ApplyStartupLayout();
        IntroductionScroll.SizeChanged += (_, _) => ApplyStartupLayout();
        EnvironmentScreen.SizeChanged += (_, _) => ApplyStartupLayout();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); _welcomeTransition?.Stop(); };
        StartupRoot.Loaded += async (_, _) => { if (_administrator) await CheckEnvironmentAsync(); };
        StartupRoot.ActualThemeChanged += (_, _) => StartupRoot.DispatcherQueue.TryEnqueue(() => { if (!_closed) TextFlyoutTheme.Refresh(StartupRoot); });
        RenderStep();
        _languageReady = true;
        if (!string.IsNullOrWhiteSpace(warning)) ShowNotice(warning, InfoBarSeverity.Warning);
    }

    private void RenderStep()
    {
        RiskModeNotice.Refresh(RiskNotice);
        BackButton.Content = L.T("返回");
        ExitButton.Content = L.T("退出");
        CompatibilityNotice.Title = L.T("兼容环境");
        CompatibilityNotice.Message = L.T("部分视觉效果与功能可能与新系统的体验不同。");
        var languagePage = _administrator && _progress.PageIndex == 1;
        var checkingPage = _administrator && !_welcomeReady && _progress.PageIndex == 0;
        EnvironmentScreen.Visibility = checkingPage ? Visibility.Visible : Visibility.Collapsed;
        IntroductionScroll.Visibility = checkingPage ? Visibility.Collapsed : Visibility.Visible;
        RenderEnvironment();
        LanguageOptions.Visibility = languagePage ? Visibility.Visible : Visibility.Collapsed;
        FeatureRows.Visibility = languagePage ? Visibility.Collapsed : Visibility.Visible;
        ApplyStartupLayout();
        if (!_administrator)
        {
            HeroIcon.Glyph = "\uE72E";
            StepEyebrow.Text = L.T("需要管理员权限");
            StepTitle.Text = L.T("以管理员身份继续");
            StepDescription.Text = L.T("Process Keeper需要管理员权限，才能完整查看应用进程并执行你确认的操作。");
            SetFeatures(
                ("\uE73E", L.T("由你确认授权"), L.T("点击继续后，Windows 将显示用户账户控制提示。")),
                ("\uE7BA", L.T("尚未开始管理进程"), L.T("当前页面不会采集、关闭或重启其他应用。")),
                ("\uE8B7", L.T("从原始程序安全启动"), _canElevate ? L.T("授权后会重新打开Process Keeper，并进入首次使用引导。") : L.T("请关闭此窗口，再打开下载的原始单文件「ProcessKeeper.exe」。")));
            StepProgress.Visibility = StepCounter.Visibility = BackButton.Visibility = Visibility.Collapsed;
            NextButton.Content = L.T("以管理员身份继续");
            NextButton.IsEnabled = _canElevate;
            ExitButton.Visibility = Visibility.Visible;
            return;
        }

        StepEyebrow.Text = _progress.IsLastPage ? L.T("准备就绪") : _review ? L.T("使用引导") : L.T("初次使用");
        StepProgress.Value = _progress.PageIndex + 1;
        StepCounter.Text = $"{_progress.PageIndex + 1} / {OnboardingProgress.PageCount}";
        BackButton.Visibility = _progress.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _progress.IsLastPage ? _review ? L.T("返回应用") : L.T("开始使用") : L.T("继续");
        ExitButton.Content = _review ? L.T("返回应用") : L.T("退出");
        ExitButton.Visibility = checkingPage || _review && !_progress.IsLastPage ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsEnabled = !_busy && !checkingPage && !_transitioning;
        switch (_progress.PageIndex)
        {
            case 0:
                HeroIcon.Glyph = "\uE80F";
                StepTitle.Text = L.T("欢迎使用Process Keeper");
                StepDescription.Text = L.T("看清正在运行的应用，留下需要的程序，再确认关闭其他程序。");
                SetFeatures(
                    ("\uE9D9", L.T("先看应用，再看进程"), L.T("单击应用可展开关联进程，查看前台、隐藏后台和占用情况。")),
                    ("\uE73E", L.T("重要应用加入白名单"), L.T("受保护的应用会保留，关闭前仍可检查本次操作。")),
                    ("\uE753", L.T("在本机完成"), L.T(BuildInfo.IsPreviewBuild ? "运行信息与规则保存在本地；测试版本不代表最终品质。" : "运行信息与规则保存在本地。")));
                break;
            case 1:
                HeroIcon.Glyph = "\uE774";
                StepTitle.Text = L.T("选择你的语言");
                StepDescription.Text = L.T("选择后立即应用。稍后也可在设置中更改。");
                AutomaticLanguageTitle.Text = L.T("自动跟随系统");
                AutomaticLanguageDescription.Text = L.T("优先系统显示语言，结合地区与时区判断。");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AutomaticLanguageRadio, AutomaticLanguageTitle.Text);
                _renderingLanguage = true;
                try
                {
                    var selectedIndex = _languagePreference switch
                    { "zh-Hans" => 1, "zh-Hant" => 2, "en" => 3, _ => 0 };
                    LanguageChoices[selectedIndex].IsChecked = true;
                }
                finally { _renderingLanguage = false; }
                break;
            case 2:
                HeroIcon.Glyph = "\uE73E";
                StepTitle.Text = L.T("把需要的应用留下");
                StepDescription.Text = L.T("正在运行或已经安装的应用，都可以加入白名单。");
                SetFeatures(
                    ("\uE710", L.T("随时添加或调整"), L.T("从运行中程序、已安装程序或白名单页，选择需要保留的应用。")),
                    ("\uE8B7", L.T("规则可带到另一台电脑"), L.T("导入后只匹配本机实际存在的应用；仍需核对匹配结果。")),
                    ("\uE74E", L.T("设置也能备份"), L.T("在设置中导入或导出规则与设置，分享前可检查文件内容。")));
                break;
            case 3:
                HeroIcon.Glyph = "\uE737";
                StepTitle.Text = L.T("让隐藏的页面出现");
                StepDescription.Text = L.T("展开进程后查看能力标签，再选择「打开对应页面」。");
                SetFeatures(
                    ("\uE8A7", L.T("先尝试恢复现有窗口"), L.T("虚拟机、无界面浏览器和托盘程序会使用对应的适配方式。")),
                    ("\uE7BA", L.T("需要重启时先询问"), L.T("AVD 等操作会说明影响，只有你确认后才会执行。")),
                    ("\uE946", L.T("以实际图形页面为准"), L.T("标签表示可尝试的能力；只有命令行窗口不算页面成功打开。")));
                break;
            default:
                HeroIcon.Glyph = "\uE73E";
                StepTitle.Text = L.T("准备好了，开始使用");
                StepDescription.Text = L.T("建议先检查白名单，再进行第一次关闭操作。");
                SetFeatures(
                    ("\uE8A5", L.T("三个列表，统一更新"), L.T("三页的实时更新开关共用设置，操作记录始终实时。")),
                    ("\uE700", L.T("给内容多留一点空间"), L.T("侧边栏左上角的菜单按钮可将导航收起为图标。")),
                    ("\uE713", L.T("按你的习惯调整"), L.T("主题与背景材质在设置里调整，操作记录会实时保留。")));
                break;
        }
    }

    private RadioButton[] LanguageChoices =>
        [AutomaticLanguageRadio, SimplifiedLanguageRadio, TraditionalLanguageRadio, EnglishLanguageRadio];

    private void ApplyStartupLayout()
    {
        var width = StartupRoot.ActualWidth;
        if (width <= 0) return;
        var padding = width < 720 ? 24 : 48;
        var compactHeight = StartupRoot.ActualHeight is > 0 and < 560;
        var compactEnvironment = compactHeight || EnvironmentScreen.ActualHeight is > 0 and < 420;
        var compactLanguage = _administrator && _progress.PageIndex == 1 &&
            (width < 480 || IntroductionScroll.ActualHeight < 560);
        // Bound the whole content column to the viewport. Text measurement or a
        // language change must never make the card's desired width grow the page.
        IntroductionContent.Width = Math.Max(0, Math.Min(960, width - padding * 2));
        IntroductionContent.Margin = new Thickness(padding, compactLanguage ? 12 : 24, padding, 20);
        StartupFooter.Margin = new Thickness(padding, 0, padding, compactHeight ? 16 : 32);
        StartupFooter.RowSpacing = compactHeight ? 10 : 16;
        EnvironmentPanel.Margin = new Thickness(padding, compactEnvironment ? 12 : 24, padding, compactEnvironment ? 12 : 24);
        EnvironmentPanel.Width = Math.Max(0, Math.Min(780, width - padding * 2));
        EnvironmentPanel.HorizontalAlignment = HorizontalAlignment.Center;
        EnvironmentPanel.Spacing = compactEnvironment ? 10 : 18;
        EnvironmentIcon.Visibility = compactEnvironment ? Visibility.Collapsed : Visibility.Visible;
        EnvironmentTitle.FontSize = compactEnvironment ? 28 : 32;
        IntroductionStack.Spacing = compactLanguage ? 12 : 20;
        HeroIcon.Visibility = compactLanguage ? Visibility.Collapsed : Visibility.Visible;
        StepEyebrow.Visibility = compactLanguage ? Visibility.Collapsed : Visibility.Visible;
        StepTitle.FontSize = compactLanguage || width < 720 ? 30 : 38;
        StepDescription.FontSize = compactLanguage ? 16 : 18;
        StepDescription.LineHeight = compactLanguage ? 24 : 28;
        LanguageOptions.Margin = new Thickness(0, compactLanguage ? 0 : 12, 0, 0);
    }

    private void LanguageCardTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs args)
    {
        if (_busy || _closed || sender is not Grid card) return;
        var choice = card.Children.OfType<RadioButton>().Single();
        choice.Focus(FocusState.Pointer);
        choice.IsChecked = true;
    }

    private async void LanguageSelectionChanged(object sender, RoutedEventArgs args)
    {
        if (!_languageReady || _renderingLanguage || _busy || _closed || !_administrator || _progress.PageIndex != 1 ||
            sender is not RadioButton selected || !int.TryParse(selected.Tag?.ToString(), out var selectedIndex) || selectedIndex is < 0 or > 3) return;
        var preference = selectedIndex switch { 1 => "zh-Hans", 2 => "zh-Hant", 3 => "en", _ => "auto" };
        // Initial selection must not overwrite the stored preference.
        if (preference == _languagePreference) return;
        _languagePreference = preference;
        L.Language = LanguageResolver.ResolveCurrent(_languagePreference);
        StartupNotice.IsOpen = false;
        RenderStep();
        SetBusy(true);
        try
        {
            await Task.Run(() =>
            {
                // Read the latest preferences before changing just the language field.
                var current = _viewStore.Load();
                _viewStore.Save(current with { Language = preference });
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (!_closed) ShowNotice(L.T("语言已临时应用，但无法保存；下次启动可能恢复原语言。"), InfoBarSeverity.Warning);
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private void SetFeatures((string Glyph, string Title, string Body) first,
        (string Glyph, string Title, string Body) second, (string Glyph, string Title, string Body) third)
    {
        FeatureIcon1.Glyph = first.Glyph; FeatureTitle1.Text = first.Title; FeatureBody1.Text = first.Body;
        FeatureIcon2.Glyph = second.Glyph; FeatureTitle2.Text = second.Title; FeatureBody2.Text = second.Body;
        FeatureIcon3.Glyph = third.Glyph; FeatureTitle3.Text = third.Title; FeatureBody3.Text = third.Body;
    }

    private void GoBack(object sender, RoutedEventArgs args)
    {
        if (_busy || !_administrator || _closed) return;
        _progress.Back();
        RenderStep();
    }

    private async void GoNext(object sender, RoutedEventArgs args)
    {
        if (_busy || _closed || _administrator && (!_welcomeReady || _transitioning)) return;
        if (!_administrator)
        {
            if (!_canElevate) return;
            SetBusy(true);
            StartupNotice.IsOpen = false;
            try
            {
                var result = await _elevate(_lifetime.Token);
                if (_closed) return;
                if (result == ElevationRequestResult.Started) { Close(); return; }
                ShowNotice(result switch
                {
                    ElevationRequestResult.Cancelled => L.T("已取消管理员授权，可以再次尝试。"),
                    ElevationRequestResult.Unavailable => L.T("启动入口已不可用，请重新打开原始单文件「ProcessKeeper.exe」。"),
                    _ => L.T("未能以管理员身份启动，请重试或重新打开原始单文件「ProcessKeeper.exe」。")
                }, InfoBarSeverity.Warning);
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception) { if (!_closed) ShowNotice(L.T("未能完成管理员授权，请重试。"), InfoBarSeverity.Warning); }
            finally { if (!_closed) SetBusy(false); }
            return;
        }

        if (!_progress.IsLastPage) { _progress.Next(); RenderStep(); return; }
        if (_review || _saveFailed) { IntroductionCompleted?.Invoke(); return; }
        SetBusy(true);
        try
        {
            await Task.Run(_store.Complete);
            if (!_closed) IntroductionCompleted?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (_closed) return;
            _saveFailed = true;
            NextButton.Content = L.T("仍然开始使用");
            ShowNotice(L.T("引导记录未保存。本次仍可继续，下次打开可能再次显示介绍。"), InfoBarSeverity.Warning);
        }
        finally { if (!_closed) SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        NextButton.IsEnabled = !busy && (_administrator ? _welcomeReady && !_transitioning : _canElevate);
        BackButton.IsEnabled = !busy;
        foreach (var choice in LanguageChoices) choice.IsEnabled = !busy;
        StartupBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StartupBusy.IsActive = busy;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        StartupNotice.Message = message;
        StartupNotice.Severity = severity;
        StartupNotice.IsOpen = true;
    }

    private void ExitStartup(object sender, RoutedEventArgs args)
    { if (_review) IntroductionCompleted?.Invoke(); else Close(); }

    private async void RecheckEnvironment(object sender, RoutedEventArgs args) => await CheckEnvironmentAsync();
    private void DismissCompatibilityNotice(InfoBar sender, object args) => CompatibilityNoticeState.Dismissed = true;

    private async Task CheckEnvironmentAsync()
    {
        if (_checkingEnvironment || _transitioning || _closed) return;
        _checkingEnvironment = true;
        EnvironmentRefresh.IsEnabled = false;
        _environmentError = null;
        _environmentReport = null;
        _environmentBlocking = false;
        EnvironmentHelp.NavigateUri = EnvironmentDownload.NavigateUri = null;
        EnvironmentHelp.Visibility = EnvironmentDownload.Visibility = EnvironmentContinue.Visibility = Visibility.Collapsed;
        EnvironmentProgress.Visibility = Visibility.Visible;
        var started = System.Diagnostics.Stopwatch.StartNew();
        RenderEnvironment();
        try
        {
            _environmentReport = await Task.Run(ProbeEnvironment, _lifetime.Token);
            if (!_environmentReport.PlatformSupported)
            {
                _environmentBlocking = true;
                _environmentError = L.T("当前系统或架构不满足现代界面的运行条件，请使用兼容入口。");
                EnvironmentHelp.NavigateUri = new Uri("https://learn.microsoft.com/en-us/windows/apps/winui/winui3/");
            }
            else if (!_environmentReport.Administrator)
            {
                _environmentBlocking = true;
                _environmentError = L.T("管理员权限：未获得");
            }
            else if (!_environmentReport.PhysicalMemory.HasValue)
                _environmentError = L.T("物理内存：无法读取");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            _environmentBlocking = ex is DllNotFoundException or BadImageFormatException or TypeLoadException;
            _environmentError = _environmentBlocking ? L.T("运行组件缺失或无法加载。请重新打开完整的 ProcessKeeper.exe。") : L.T("部分运行环境无法读取，可重新检测。");
            if (_environmentBlocking)
            {
                EnvironmentHelp.NavigateUri = new Uri("https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps");
            }
        }
        finally
        {
            _checkingEnvironment = false;
            if (!_closed)
            {
                EnvironmentProgress.Visibility = Visibility.Collapsed;
                EnvironmentRefresh.IsEnabled = true;
                EnvironmentHelp.Visibility = _environmentError is not null && EnvironmentHelp.NavigateUri is not null ? Visibility.Visible : Visibility.Collapsed;
                EnvironmentDownload.Visibility = _environmentError is not null && EnvironmentDownload.NavigateUri is not null ? Visibility.Visible : Visibility.Collapsed;
                EnvironmentContinue.Visibility = _environmentError is not null && !_environmentBlocking ? Visibility.Visible : Visibility.Collapsed;
                RenderEnvironment();
            }
        }
        if (_closed || _environmentError is not null || _environmentReport is null) return;
        try
        {
            if (started.ElapsedMilliseconds < 300) await Task.Delay(300 - (int)started.ElapsedMilliseconds, _lifetime.Token);
            await RevealWelcomeAsync();
        }
        catch (OperationCanceledException) when (_closed) { }
    }

    private void RenderEnvironment()
    {
        EnvironmentTitle.Text = L.T("运行环境检测");
        EnvironmentRefresh.Content = L.T("重新检测");
        EnvironmentDetails.Text = _checkingEnvironment ? L.T("正在读取运行环境…") :
            _environmentError is not null ? (_environmentReport?.Description + "\n\n" + _environmentError).Trim() :
            _environmentReport?.Description ?? L.T("正在读取运行环境…");
        EnvironmentDownload.Content = L.T("打开官方下载页面");
        EnvironmentHelp.Content = L.T("查看官方解决说明");
        EnvironmentContinue.Content = L.T("仍然继续");
        try
        {
            var materials = new List<string>();
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()) materials.Add("Mica");
            if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) materials.Add("Acrylic");
            EnvironmentGraphics.Text = materials.Count > 0 ? L.F($"原生材质支持：{string.Join(" | ", materials)}") : L.T("当前环境使用纯色背景。");
        }
        catch { EnvironmentGraphics.Text = L.T("原生材质能力无法读取，将使用纯色背景。"); }
    }

    private async void ContinueAfterEnvironmentWarning(object sender, RoutedEventArgs args)
    { if (!_environmentBlocking && !_checkingEnvironment && !_transitioning) await RevealWelcomeAsync(); }

    private async Task RevealWelcomeAsync()
    {
        if (_closed || _welcomeReady || _transitioning) return;
        _transitioning = true;
        EnvironmentScreen.IsHitTestVisible = false;
        IntroductionScroll.Visibility = Visibility.Visible;
        IntroductionScroll.Opacity = 0;
        try
        {
            if (new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                var transition = new Storyboard();
                _welcomeTransition = transition;
                foreach (var (target, from, to) in new[] { ((DependencyObject)EnvironmentScreen, 1d, 0d), ((DependencyObject)IntroductionScroll, 0d, 1d) })
                {
                    var fade = new DoubleAnimation { From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
                    Storyboard.SetTarget(fade, target); Storyboard.SetTargetProperty(fade, "Opacity"); transition.Children.Add(fade);
                }
                var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                transition.Completed += (_, _) => finished.TrySetResult(true);
                using var registration = _lifetime.Token.Register(() => finished.TrySetCanceled(_lifetime.Token));
                transition.Begin();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(2), _lifetime.Token);
                transition.Stop();
            }
            if (!_closed) _welcomeReady = true;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception) { if (!_closed) _welcomeReady = true; }
        finally
        {
            _welcomeTransition?.Stop();
            _transitioning = false;
            _welcomeTransition = null;
            if (!_closed)
            {
                IntroductionScroll.Opacity = EnvironmentScreen.Opacity = 1;
                EnvironmentScreen.IsHitTestVisible = true;
                RenderStep();
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
