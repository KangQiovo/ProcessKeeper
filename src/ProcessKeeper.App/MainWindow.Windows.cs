using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private bool _windowOperationRunning;

    private static string WindowStateLabel(WindowRecord window) => window.IsCloaked ? L.T("其他桌面 / 系统隐藏") :
        !window.IsVisible ? L.T("隐藏") : window.IsMinimized ? L.T("已最小化") : L.T("可见");

    private sealed record WindowChoice(ProcessRecord Process, WindowRecord Window, bool IsAvdPage)
    {
        public bool IsRecommended { get; init; }
        public string Label => $"{WindowStateLabel(Window)} | {(string.IsNullOrWhiteSpace(Window.Title) ? Process.Name : Window.Title)} | PID {Process.Id}";
    }

    private static WindowChoice[] WindowCandidates(ApplicationGroup app, bool hiddenOnly = false) => app.Processes
        .SelectMany(process => process.Windows
            .Where(window => WindowActions.IsCandidate(process, window, includeHidden: hiddenOnly, applicationKey: app.Key) && (!hiddenOnly || !window.IsVisible))
            .Select(window => new WindowChoice(process, window, AvdGuiMatcher.RequiresNativeGui(process, app.Key))))
        .OrderByDescending(choice => choice.IsAvdPage).ThenByDescending(choice => choice.Window.IsForeground)
        .ThenBy(choice => choice.Window.IsMinimized).ThenBy(choice => choice.Process.Id).ThenBy(choice => choice.Window.Handle)
        .Select((choice, index) => choice with { IsRecommended = choice.IsAvdPage && index == 0 }).ToArray();

    private static StackPanel WindowChoiceRow(WindowChoice choice)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (choice.IsAvdPage)
        {
            var recommendation = new TextBlock { Text = choice.IsRecommended ? L.T("推荐") : L.T("AVD画面"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(recommendation, L.T("已识别为 AVD 原生设备页面候选；显示前仍会重新核实，不代表已经打开成功。"));
            row.Children.Add(recommendation);
        }
        row.Children.Add(new TextBlock { Text = choice.Label, TextWrapping = TextWrapping.Wrap, MaxWidth = choice.IsAvdPage ? 392 : 456 });
        return row;
    }

    private async void ShowSelectedWindow(object sender, RoutedEventArgs e) => await OperateWindow(false);
    private async void MinimizeSelectedWindow(object sender, RoutedEventArgs e) => await OperateWindow(true);
    private async void RevealSelectedHiddenWindow(object sender, RoutedEventArgs e) => await OperateWindow(false, hiddenOnly: true);

    private static string WindowUnavailableMessage(ApplicationGroup app)
    {
        if (app.Key == "known:avd")
        {
            var headless = app.Processes.Any(process => process.Name.Contains("-headless", StringComparison.OrdinalIgnoreCase));
            return (headless ? L.T("检测到 headless 安卓模拟器核心，目前没有可恢复的原生主窗口。") : L.T("未找到可恢复的安卓模拟器主窗口。")) +
                L.T("可使用“以原生窗口重启 AVD”，确认后自动重新启动。嵌入 Android Studio 的窗口也可从 Running Devices 查看。");
        }
        return app.Processes.Any(process => process.Windows.Any(window => window.IsCloaked))
            ? L.T("检测到其他虚拟桌面或被系统隐藏的窗口，请先切换到对应桌面。此处只能恢复当前桌面上已经存在的普通主窗口。")
            : L.T("没有找到可恢复的主窗口。后台服务和辅助进程可能从未创建界面；托盘程序也可能需要点击托盘图标重新创建窗口。请先从程序自身的入口打开页面，再刷新列表。");
    }

    private async Task OperateWindow(bool minimize, bool hiddenOnly = false, ApplicationGroup? target = null)
    {
        var app = target ?? SelectedApp;
        if (_working || _dialogOpen || _closed || _windowOperationRunning || app is null) return;
        _windowOperationRunning = true;
        try
        {
            var windows = WindowCandidates(app, hiddenOnly);
            if (!minimize && !hiddenOnly && windows.Length == 0)
            {
                hiddenOnly = true;
                windows = WindowCandidates(app, hiddenOnly: true);
            }
            if (windows.Length == 0)
            {
                if (!minimize && app.Key == "known:avd")
                {
                    await ConfirmAndRestartAvdAsync(app);
                    return;
                }
                if (!minimize && HasSpecialEntry(app))
                {
                    await OpenSpecialWindowAsync(app);
                    return;
                }
                ShowNotice(L.T("没有可操作的窗口"), minimize ? L.T("当前没有可最小化的可见窗口，请刷新状态。") : WindowUnavailableMessage(app), InfoBarSeverity.Informational);
                return;
            }
            var choice = windows[0];
            if (hiddenOnly || windows.Length > 1)
            {
                var list = new ListView { ItemsSource = windows.Select(WindowChoiceRow).ToArray(), SelectionMode = ListViewSelectionMode.Single, SelectedIndex = 0, MaxHeight = 320 };
                var panel = new StackPanel { Spacing = 12, Width = 500 };
                panel.Children.Add(new TextBlock
                {
                    Text = hiddenOnly
                        ? L.F($"{app.Name} 有 {windows.Length} 个可尝试恢复的隐藏主窗口。选择窗口后，Process Keeper会请求显示并检查实际结果。程序可能自行再次隐藏窗口。")
                        : L.F($"{app.Name} 有 {windows.Length} 个窗口，请选择要{(minimize ? L.T("最小化") : L.T("显示"))}的窗口。"),
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(list);
                if (await ShowDialog(NewDialog(hiddenOnly ? L.T("显示隐藏窗口") : L.T("选择窗口"), panel,
                    minimize ? L.T("最小化") : hiddenOnly ? L.T("尝试显示") : L.T("显示窗口"))) != ContentDialogResult.Primary ||
                    list.SelectedIndex < 0 || _closed) return;
                choice = windows[list.SelectedIndex];
            }
            var result = await Task.Run(() => minimize ? WindowActions.Minimize(choice.Process, choice.Window, app.Key) :
                hiddenOnly ? WindowActions.RevealHidden(choice.Process, choice.Window, app.Key) : WindowActions.Activate(choice.Process, choice.Window, app.Key));
            if (_closed) return;
            Log($"{(minimize ? L.T("最小化窗口") : hiddenOnly ? L.T("显示隐藏窗口") : L.T("显示窗口"))} | {app.Name} | PID {choice.Process.Id} | {result.Message}");
            ShowNotice(L.T("窗口操作"), result.Message, result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            await RefreshAsync();
        }
        catch (Exception ex) { if (!_closed) ShowNotice(L.T("无法操作窗口"), ex.Message, InfoBarSeverity.Warning); }
        finally { _windowOperationRunning = false; }
    }
}
