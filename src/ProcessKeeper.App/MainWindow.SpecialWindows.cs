using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly SpecialWindowService _specialWindows = new();
    private readonly ChromiumLiveService _browserLive = new();
    private readonly List<BrowserLiveWindow> _browserWindows = [];
    private CancellationTokenSource? _specialCancellation;
    private sealed record RecoveryChoice(string Label, SpecialWindowPlan? Plan, BrowserPageTarget? Page);

    private async void OpenSelectedSpecialWindow(object sender, RoutedEventArgs args)
    {
        if (_working || _closed || _dialogOpen || _windowOperationRunning || SelectedApp is not { } app) return;
        _windowOperationRunning = true;
        try { await OpenSpecialWindowAsync(app); }
        finally { _windowOperationRunning = false; }
    }
    private async Task OpenSpecialWindowAsync(ApplicationGroup app)
    {
        if (_working || _dialogOpen || _closed) return;
        _working = true;
        using var cancellation = new CancellationTokenSource();
        _specialCancellation = cancellation;
        var cancel = new Button { Content = L.T("取消") };
        cancel.Click += (_, _) => cancellation.Cancel();
        Navigation.IsEnabled = CloseOthersButton.IsEnabled = CloseSelectedButton.IsEnabled = AddSelectedButton.IsEnabled =
            ShowWindowButton.IsEnabled = MoreButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        try
        {
            ShowNotice(L.T("检查界面入口"), L.T("正在核实所选进程的可用方式…"), InfoBarSeverity.Informational);
            Notice.ActionButton = cancel;
            var nativeTask = _specialWindows.DiscoverAsync(app.Processes, cancellation.Token);
            var browserTask = _browserLive.DiscoverAsync(app.Processes, cancellation.Token);
            await Task.WhenAll(nativeTask, browserTask);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed) return;
            Notice.ActionButton = null;
            var native = await nativeTask; var browser = await browserTask;
            var options = native.Plans.Select(p => new RecoveryChoice(p.Name + L.T(" | 唤起界面"), p, null))
                .Concat(browser.Targets.Select(p => new RecoveryChoice(L.T("实时画面 | ") + (p.Title.Length > 0 ? p.Title : L.T("浏览器页面")), null, p))).ToArray();
            if (options.Length == 0)
            {
                var message = string.Join("\n", native.Notices.Concat(browser.Notices).Take(3));
                ShowNotice(L.T("未找到可用入口"), message.Length > 0 ? message : L.T("此进程没有可恢复窗口或已支持的界面入口。纯后台服务可能没有页面。"), InfoBarSeverity.Informational);
                return;
            }
            var picker = new ComboBox { ItemsSource = options.Select(p => p.Label).ToArray(), SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch };
            var detail = new TextBlock { TextWrapping = TextWrapping.Wrap };
            void UpdateExplanation() => detail.Text = picker.SelectedIndex < 0 ? "" : options[picker.SelectedIndex] is { Plan: { } plan }
                ? plan.Explanation + L.T("\n确认后只发送一次请求，并等待实际窗口；不会自动重启后台核心。")
                : L.T("显示原无头页面的只读实时画面。原任务继续运行；关闭预览只断开画面连接。");
            picker.SelectionChanged += (_, _) => UpdateExplanation(); UpdateExplanation();
            var panel = new StackPanel { Spacing = 12, Width = 500 }; panel.Children.Add(picker); panel.Children.Add(detail);
            if (await ShowDialog(NewDialog(L.T("打开对应界面"), panel, L.T("确认打开"))) != ContentDialogResult.Primary || picker.SelectedIndex < 0 || _closed)
            { if (!_closed) Notice.IsOpen = false; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            var choice = options[picker.SelectedIndex];
            if (choice.Plan is { } selected)
            {
                Log(L.F($"用户确认专用界面 | {selected.Name} | PID {selected.Source.Id}"));
                var progress = new Progress<string>(text =>
                {
                    if (!ReferenceEquals(_specialCancellation, cancellation)) return;
                    Log(L.T("专用界面 | ") + text);
                    if (_closed) return;
                    ShowNotice(L.T("唤起界面"), text, InfoBarSeverity.Informational); cancel.Content = L.T("停止等待"); Notice.ActionButton = cancel;
                });
                var result = await Task.Run(() => _specialWindows.ExecuteAsync(selected, true, progress, cancellation.Token));
                Log(L.F($"专用界面 | {selected.Name} | {result.Message}"));
                if (!_closed) ShowNotice(result.Success ? L.T("对应窗口已显示") : L.T("窗口核验结果"), result.Message,
                    result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
            else if (choice.Page is { } page)
            {
                Log(L.F($"用户确认无头页面预览 | PID {page.Source.Id}"));
                ShowNotice(L.T("连接页面画面"), L.T("正在读取并核验真实图像…"), InfoBarSeverity.Informational); Notice.ActionButton = cancel;
                var session = await _browserLive.ConnectAsync(page, cancellation.Token);
                if (_closed) { await session.DisposeAsync(); return; }
                var window = new BrowserLiveWindow(page.Title, Root.ActualTheme);
                _browserWindows.Add(window); window.Closed += (_, _) => _browserWindows.Remove(window);
                window.Activate();
                bool displayed = await window.StartAsync(session, cancellation.Token);
                Log(L.F($"无头浏览器画面 | PID {page.Source.Id} | {(displayed ? L.T("真实图像已显示") : L.T("图像未显示"))}"));
                if (!_closed) ShowNotice(displayed ? L.T("实时画面已显示") : L.T("未能显示画面"),
                    displayed ? L.T("已在独立窗口显示原页面的只读实时画面。") : L.T("未获取并显示有效页面图像，详情见预览窗口。"),
                    displayed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
        }
        catch (OperationCanceledException) { if (!_closed) ShowNotice(L.T("已取消"), L.T("已停止后续操作；已经打开的窗口保留。"), InfoBarSeverity.Informational); }
        catch (Exception exception) { Log(L.T("专用界面未完成 | ") + exception.Message); if (!_closed) ShowNotice(L.T("无法打开界面"), exception.Message, InfoBarSeverity.Warning); }
        finally
        {
            _specialCancellation = null;
            _working = false;
            if (!_closed)
            {
                Notice.ActionButton = null; LoadingRing.IsActive = false; Navigation.IsEnabled = true;
                RenderApps(); await RefreshAsync();
            }
        }
    }

    private async Task OpenProcessPageAsync(ProcessRecord process)
    {
        if (_working || _closed || _dialogOpen || _windowOperationRunning) return;
        var scope = new List<ProcessRecord> { process };
        if (process.ApplicationKey == "known:avd" && !process.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase))
        {
            var launcher = _snapshot.Processes.FirstOrDefault(p => p.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase) &&
                p.Id == process.ParentId && process.ParentIdentityVerified && p.StartTimeUtcTicks > 0 &&
                p.StartTimeUtcTicks <= process.StartTimeUtcTicks && p.OwnerSid == process.OwnerSid && p.SessionId == process.SessionId);
            if (launcher is not null) scope.Add(launcher);
        }
        var app = new ApplicationGroup { Key = process.ApplicationKey, Name = process.ApplicationName.Length > 0 ? process.ApplicationName : process.Name, Processes = scope };
        await OperateWindow(false, target: app);
    }
    private static bool HasSpecialEntry(ApplicationGroup app) => app.Processes.Any(p => SpecialWindowPolicy.IsSupportedName(p.Name) ||
        p.Name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase));
    private void CloseRecoveryWindows()
    {
        _specialCancellation?.Cancel();
        foreach (var window in _browserWindows.ToArray()) window.Close();
    }
}
