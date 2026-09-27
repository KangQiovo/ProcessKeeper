using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly AvdRestartService _avdRestarter = new();
    private CancellationTokenSource? _avdCancellation;

    private async void RestartSelectedAvd(object sender, RoutedEventArgs args)
    {
        if (_working || _dialogOpen || _closed || _windowOperationRunning || SelectedApp is not { Key: "known:avd" } app) return;
        _windowOperationRunning = true;
        try { await ConfirmAndRestartAvdAsync(app); }
        finally { _windowOperationRunning = false; }
    }

    private async Task ConfirmAndRestartAvdAsync(ApplicationGroup app)
    {
        if (_working || _dialogOpen || _closed) return;
        _working = true;
        _avdCancellation = new CancellationTokenSource();
        var cancellation = _avdCancellation;
        var cancelButton = new Button { Content = L.T("取消") };
        cancelButton.Click += (_, _) => cancellation.Cancel();
        Navigation.IsEnabled = AppsList.IsEnabled = CloseOthersButton.IsEnabled = CloseSelectedButton.IsEnabled =
            AddSelectedButton.IsEnabled = ShowWindowButton.IsEnabled = MoreButton.IsEnabled = false;
        LoadingRing.IsActive = true;
        try
        {
            ShowNotice(L.T("识别 AVD"), L.T("正在核实设备和启动方式，确认前不会退出模拟器。"), InfoBarSeverity.Informational);
            Notice.ActionButton = cancelButton;
            var discovery = await Task.Run(() => _avdRestarter.DiscoverAsync(app.Processes, cancellation.Token));
            if (_closed) return;
            cancellation.Token.ThrowIfCancellationRequested();
            Notice.ActionButton = null;
            foreach (var message in discovery.Notices) Log(L.T("AVD 识别 | ") + message);
            if (discovery.Plans.Count == 0)
            {
                ShowNotice(L.T("暂时无法自动重启"), discovery.Notices.FirstOrDefault() ?? L.T("没有找到可核实的设备，请刷新后重试。"), InfoBarSeverity.Warning);
                return;
            }
            var plans = discovery.Plans;
            var picker = new ComboBox
            {
                ItemsSource = plans.Select(plan => $"{plan.AvdName} | emulator-{plan.ConsolePort}").ToArray(),
                SelectedIndex = Math.Max(0, plans.ToList().FindIndex(plan => plan.Engine.Id == _selectedProcessId || plan.Launcher.Id == _selectedProcessId)),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var details = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 };
            void UpdateDetails()
            {
                if (picker.SelectedIndex < 0) return;
                var plan = plans[picker.SelectedIndex];
                details.Text = L.F($"原进程：{plan.Launcher.Id} / {plan.Engine.Id}\nSDK：{plan.Launcher.Path}\n") +
                    L.T("沿用原设备目录和运行参数，改为原生窗口冷启动。\n") + string.Join("\n", plan.Changes);
            }
            picker.SelectionChanged += (_, _) => UpdateDetails();
            UpdateDetails();
            var panel = new StackPanel { Spacing = 12, Width = 500 };
            panel.Children.Add(new TextBlock { Text = L.T("将退出并重新启动所选 AVD。正在运行的任务会中断，请先保存。"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(picker);
            panel.Children.Add(new Expander { Header = L.T("启动详情"), Content = details, HorizontalAlignment = HorizontalAlignment.Stretch });
            if (await ShowDialog(NewDialog(L.T("以原生窗口重启 AVD"), panel, L.T("确认重启"))) != ContentDialogResult.Primary || picker.SelectedIndex < 0 || _closed)
            {
                Log(L.T("AVD 重启已取消 | 尚未发送退出或启动请求。"));
                if (!_closed) Notice.IsOpen = false;
                return;
            }
            var selected = plans[picker.SelectedIndex];
            Log(L.F($"用户确认 AVD 原生窗口重启 | {selected.AvdName} | 原 PID {selected.Engine.Id} | 端口 {selected.ConsolePort}"));
            var progress = new Progress<AvdRestartProgress>(update =>
            {
                if (!ReferenceEquals(_avdCancellation, cancellation)) return;
                Log($"AVD {selected.AvdName} | {update.Stage} | {update.Message}");
                if (_closed) return;
                ShowNotice(update.Stage, update.Message, InfoBarSeverity.Informational);
                cancelButton.Content = update.Stage == L.T("等待真实窗口") ? L.T("停止等待") : L.T("取消后续操作");
                Notice.ActionButton = cancelButton;
            });
            var result = await Task.Run(() => _avdRestarter.ExecuteAsync(selected, progress, cancellation.Token));
            Log($"AVD {selected.AvdName} | {result.Message}");
            if (_closed) return;
            ShowNotice(result.Success ? L.T("原生窗口已显示") : L.T("AVD 重启结果"), result.Message,
                result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { if (!_closed) ShowNotice(L.T("已取消识别"), L.T("尚未发送退出或启动请求。"), InfoBarSeverity.Informational); }
        catch (Exception exception) { Log(L.T("AVD 操作未完成 | ") + exception.Message); if (!_closed) ShowNotice(L.T("AVD 操作未完成"), exception.Message, InfoBarSeverity.Warning); }
        finally
        {
            _avdCancellation = null;
            cancellation.Dispose();
            _working = false;
            if (!_closed)
            {
                Notice.ActionButton = null;
                LoadingRing.IsActive = false;
                Navigation.IsEnabled = AppsList.IsEnabled = true;
                RenderApps();
                await RefreshAsync();
            }
        }
    }
}
