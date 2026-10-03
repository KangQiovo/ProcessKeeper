using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private bool _checkingCompatibility;

    private async void CheckCompatibility(object sender, RoutedEventArgs args)
    {
        if (_closed || _checkingCompatibility || _dialogOpen || _working || _windowOperationRunning ||
            _updatesView?.IsBusy == true || _autorunsView?.IsChanging == true || _uninstallView?.IsBusy == true) return;
        _checkingCompatibility = true;
        CompatibilityCheckButton.IsEnabled = false;
        CompatibilityCheckButton.Content = L.T("正在读取运行环境…");
        try
        {
            RuntimeEnvironmentReport? report = null;
            string? error = null, help = null;
            try { report = await Task.Run(RuntimeEnvironmentProbe.Capture); }
            catch (Exception ex)
            {
                bool component = ex is DllNotFoundException or BadImageFormatException or TypeLoadException;
                error = component ? L.T("运行组件缺失或无法加载。请重新打开完整的 ProcessKeeper.exe。") :
                    L.T("部分运行环境无法读取，可重新检测。");
                help = component ? "https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps" :
                    "https://learn.microsoft.com/en-us/windows/apps/winui/winui3/";
            }
            if (_closed || _dialogOpen || _working || _windowOperationRunning ||
                _updatesView?.IsBusy == true || _autorunsView?.IsChanging == true || _uninstallView?.IsBusy == true) return;
            if (report is not null)
            {
                if (!report.PlatformSupported)
                {
                    error = L.T("当前系统或架构不满足现代界面的运行条件，请使用兼容入口。");
                    help = "https://learn.microsoft.com/en-us/windows/apps/winui/winui3/";
                }
                else if (!report.Administrator)
                {
                    error = L.T("管理员权限：未获得");
                    help = "https://learn.microsoft.com/en-us/windows/security/application-security/application-control/user-account-control/";
                }
                else if (!report.PhysicalMemory.HasValue)
                {
                    error = L.T("物理内存：无法读取");
                    help = "https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex";
                }
            }
            var body = new StackPanel { Spacing = 12, MinWidth = 280, MaxWidth = 520 };
            body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false,
                Severity = error is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                Title = L.T(error is null ? "环境检测通过" : "环境检测需要关注"), Message = error ?? "" });
            var details = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                MinHeight = 140, MaxHeight = 280, VerticalContentAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(0) };
            ScrollViewer.SetVerticalScrollBarVisibility(details, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(details, ScrollBarVisibility.Disabled);
            // Configure the multiline report and its scrolling before populating it.
            details.Text = report?.Description ?? L.T("部分运行环境无法读取，可重新检测。");
            body.Children.Add(details);
            var dialog = NewDialog(L.T("运行环境检测"), new ScrollViewer { Content = body, MaxHeight = 400,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }, help is null ? "" : L.T("查看官方解决说明"));
            dialog.CloseButtonText = L.T("关闭");
            var decision = await ShowDialog(dialog);
            if (decision != ContentDialogResult.Primary || help is null || _closed) return;
            var confirmation = NewDialog(L.T("打开项目主页？"), new TextBlock {
                Text = L.T("将在默认浏览器打开以下网址：") + "\n\n" + help,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 480 }, L.T("打开浏览器"));
            if (await ShowDialog(confirmation) == ContentDialogResult.Primary && !_closed)
            {
                if (!await Windows.System.Launcher.LaunchUriAsync(new Uri(help)))
                    ShowNotice(L.T("无法打开浏览器"), help, InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex) { if (!_closed) ShowNotice(L.T("环境检测需要关注"), ex.Message, InfoBarSeverity.Warning); }
        finally
        {
            _checkingCompatibility = false;
            if (!_closed) { CompatibilityCheckButton.IsEnabled = true; CompatibilityCheckButton.Content = L.T("运行环境检测"); }
        }
    }
}
