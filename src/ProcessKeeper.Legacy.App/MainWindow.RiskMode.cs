using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private bool _riskModeDialogOpen;
    private void RiskModeChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(() => RiskModeChanged(sender, args))); return; }
        if (_closed) return;
        UpdateRiskModeUi();
    }
    private void UpdateRiskModeUi()
    {
        RiskNoticeHost.Children.Clear();
        if (RiskConfirmationMode.IsEnabled)
            RiskNoticeHost.Children.Add(new Wpf.Ui.Controls.InfoBar
            {
                Tag = "risk-mode-banner", IsOpen = true, IsClosable = false, Severity = Wpf.Ui.Controls.InfoBarSeverity.Error,
                Title = L.T("无视风险模式"), Message = L.T("二次风险确认已关闭。结束部分程序可能丢失数据、使桌面失效，甚至导致系统崩溃。"),
                Margin = new Thickness(12, 8, 12, 0), HorizontalAlignment = HorizontalAlignment.Stretch
            });
        foreach (var button in FindRiskButtons(SettingsContent))
            button.Content = new TextBlock { Text = L.T(RiskConfirmationMode.IsEnabled ? "恢复风险确认" : "无视风险模式"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    }
    private static IEnumerable<Button> FindRiskButtons(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button button && Equals(button.Tag, "risk-mode-toggle")) yield return button;
            foreach (var descendant in FindRiskButtons(child)) yield return descendant;
        }
    }
    private async Task ToggleRiskModeAsync()
    {
        if (_closed || _life.IsCancellationRequested) return;
        if (RiskConfirmationMode.IsEnabled) { RiskConfirmationMode.Disable(); Log(L.T("无视风险模式已关闭")); return; }
        if (_busy || _riskModeDialogOpen) return;
        _riskModeDialogOpen = true;
        try
        {
            if (!await ShowRiskModeDialogAsync(_life.Token) || _closed || _life.IsCancellationRequested) return;
            RiskConfirmationMode.Enable(); Log(L.T("无视风险模式已启用"));
        }
        finally { _riskModeDialogOpen = false; }
    }
    private async Task<bool> ShowRiskModeDialogAsync(CancellationToken cancellationToken)
    {
        if (_closed || cancellationToken.IsCancellationRequested) return false;
        var area = _backend.WorkingArea();
        var dialog = new Window
        {
            Owner = this, Title = "Process Keeper | " + L.T("启用无视风险模式"), Width = Math.Min(620, Math.Max(1, area.Width - 16)),
            MaxHeight = Math.Max(1, area.Height - 16), SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Background = (Brush)Resources["PageBrush"], Foreground = (Brush)Resources["InkBrush"]
        };
        var root = new Grid { Margin = new Thickness(24), Background = dialog.Background };
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel(); content.Children.Add(Text(L.T("启用无视风险模式"), 24));
        content.Children.Add(Text(L.T("将跳过敏感关闭的 5 秒倒计时和强制关闭二次确认。首次操作确认与系统核心保护仍保留。")));
        content.Children.Add(Text(L.T("关闭部分程序可能导致桌面失效、未保存数据丢失，甚至系统崩溃。请先保存所有工作。")));
        content.Children.Add(Text(L.T("仅本次运行生效；重新启动后恢复风险确认。")));
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(actions, 1); root.Children.Add(actions);
        var cancel = new Button { Tag = "risk-mode-cancel", Content = L.T("取消"), Style = (Style)Resources[typeof(Button)], IsCancel = true, IsDefault = true };
        var confirm = new Button { Tag = "risk-mode-confirm", Content = L.F($"启用无视风险（{10} 秒）"), Style = (Style)Resources[typeof(Button)], IsEnabled = false, Background = new SolidColorBrush(Color.FromRgb(176, 0, 32)), Foreground = Brushes.White };
        actions.Children.Add(cancel); actions.Children.Add(confirm); dialog.Content = root;
        var elapsed = new Stopwatch(); var started = false; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool Eligible() => started && elapsed.Elapsed >= TimeSpan.FromSeconds(10) && !cancellationToken.IsCancellationRequested && !_closed;
        void Update()
        {
            var remaining = started ? Math.Max(0, (int)Math.Ceiling(10 - elapsed.Elapsed.TotalSeconds)) : 10;
            confirm.Content = remaining > 0 ? L.F($"启用无视风险（{remaining} 秒）") : L.T("确认启用"); confirm.IsEnabled = Eligible();
        }
        dialog.ContentRendered += (_, _) => { if (started) return; started = true; elapsed.Start(); timer.Start(); Update(); };
        timer.Tick += (_, _) => Update(); cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) => { if (Eligible()) dialog.DialogResult = true; }; dialog.Closed += (_, _) => timer.Stop();
        using var cancellation = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.DialogResult = false; })));
        try { await Task.Yield(); if (_closed || cancellationToken.IsCancellationRequested) return false; return dialog.ShowDialog() == true && Eligible(); }
        finally { timer.Stop(); elapsed.Stop(); }
    }
}
