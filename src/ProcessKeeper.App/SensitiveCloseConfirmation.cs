using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class SensitiveCloseConfirmation
{
    internal static async Task<bool> ShowAsync(XamlRoot root, ElementTheme theme, ProcessRecord target,
        Func<ContentDialog, Task<ContentDialogResult>> showDialog, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        var panel = new StackPanel { Spacing = 14, MaxWidth = 510 };
        panel.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Message = L.T("关闭桌面、开始菜单或输入组件可能导致桌面、任务栏或输入功能暂时不可用，并中断正在进行的任务或丢失未保存的数据。")
        });
        panel.Children.Add(new TextBlock { Text = $"{target.ApplicationName} | {target.Name} | PID {target.Id}\n{target.Path}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        panel.Children.Add(new TextBlock { Text = L.T("风险与免责说明"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("此操作由你主动选择。请先保存工作；Process Keeper 无法保证界面自动恢复，也无法找回丢失的数据。"), TextWrapping = TextWrapping.Wrap });
        var acknowledge = new CheckBox
        {
            Content = new TextBlock { Text = L.T("我已了解上述风险并确认继续"), TextWrapping = TextWrapping.Wrap },
            IsChecked = false, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(acknowledge);
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme, Title = L.T("敏感操作 | 二次确认"),
            Content = new ScrollViewer { Content = panel, MaxHeight = Math.Max(100, Math.Min(420, root.Size.Height - 200)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = L.F($"确认强制关闭（{5} 秒）"), IsPrimaryButtonEnabled = false,
            CloseButtonText = L.T("保留运行"), DefaultButton = ContentDialogButton.Close
        };
        var elapsed = new Stopwatch();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool Ready() => elapsed.IsRunning && elapsed.Elapsed >= TimeSpan.FromSeconds(5) && acknowledge.IsChecked == true && !cancellationToken.IsCancellationRequested;
        void Update()
        {
            var remaining = Math.Max(0, (int)Math.Ceiling(5 - elapsed.Elapsed.TotalSeconds));
            dialog.PrimaryButtonText = remaining > 0 ? L.F($"确认强制关闭（{remaining} 秒）") : L.T("确认强制关闭");
            dialog.IsPrimaryButtonEnabled = Ready();
        }
        timer.Tick += (_, _) => Update();
        acknowledge.Checked += (_, _) => Update();
        acknowledge.Unchecked += (_, _) => Update();
        dialog.Opened += (_, _) => { elapsed.Restart(); Update(); timer.Start(); };
        dialog.Closed += (_, _) => timer.Stop();
        dialog.PrimaryButtonClick += (_, args) => { if (!Ready()) args.Cancel = true; };
        using var cancellation = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        try { return await showDialog(dialog) == ContentDialogResult.Primary && Ready(); }
        finally { timer.Stop(); elapsed.Stop(); }
    }
}
