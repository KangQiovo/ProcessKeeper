using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal static class RiskModeConfirmation
{
    internal static async Task<bool> ShowAsync(XamlRoot root, ElementTheme theme,
        Func<ContentDialog, Task<ContentDialogResult>> showDialog, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        var body = new StackPanel { Spacing = 14, MaxWidth = 510 };
        body.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error,
            Message = L.T("关闭部分程序可能导致桌面失效、未保存数据丢失，甚至系统崩溃。请先保存所有工作。") });
        body.Children.Add(new TextBlock { Text = L.T("将跳过敏感关闭的 5 秒倒计时和强制关闭二次确认。首次操作确认与系统核心保护仍保留。"), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = L.T("仅本次运行生效；重新启动后恢复风险确认。"), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = root, RequestedTheme = theme, Title = L.T("启用无视风险模式"),
            Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(100, Math.Min(420, root.Size.Height - 200)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            PrimaryButtonText = L.F($"启用无视风险（{10} 秒）"), IsPrimaryButtonEnabled = false,
            CloseButtonText = L.T("取消"), DefaultButton = ContentDialogButton.Close
        };
        var elapsed = new Stopwatch();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool Ready() => elapsed.IsRunning && elapsed.Elapsed >= TimeSpan.FromSeconds(10) && !token.IsCancellationRequested;
        void Update()
        {
            var remaining = Math.Max(0, (int)Math.Ceiling(10 - elapsed.Elapsed.TotalSeconds));
            dialog.PrimaryButtonText = remaining > 0 ? L.F($"启用无视风险（{remaining} 秒）") : L.T("确认启用");
            dialog.IsPrimaryButtonEnabled = Ready();
        }
        timer.Tick += (_, _) => Update();
        dialog.Opened += (_, _) => { elapsed.Restart(); Update(); timer.Start(); };
        dialog.Closed += (_, _) => timer.Stop();
        dialog.PrimaryButtonClick += (_, args) => { if (!Ready()) args.Cancel = true; };
        using var cancellation = token.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        try { return await showDialog(dialog) == ContentDialogResult.Primary && Ready(); }
        finally { timer.Stop(); elapsed.Stop(); }
    }
}
