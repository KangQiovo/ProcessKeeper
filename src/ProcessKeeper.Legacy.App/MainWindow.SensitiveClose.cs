using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private async Task CloseSensitiveTargetAsync(ProcessRecord target)
    {
        if (_busy || _closed || !_rulesReadable || !_backend.IsAdministrator || !SensitiveProcessClose.IsSensitiveComponent(target)) return;
        _busy = true;
        try
        {
            var initial = _backend.Policy.EvaluateSensitiveClose(target, _snapshot, RunningRules);
            if (initial.Protected) { Notice(L.T("敏感进程仍受保护") + " | " + initial.Reason); return; }
            if (!await Confirm(L.T("确认关闭敏感进程"), L.T(RiskConfirmationMode.IsEnabled ? "此操作仅针对下列进程。无视风险模式已跳过二次确认。" : "此操作仅针对下列进程。继续后还需确认风险。") + "\n\n" + target.Name + " | PID " + target.Id + "\n" + target.Path)) return;
            if (_closed || _life.IsCancellationRequested || (!RiskConfirmationMode.IsEnabled && !await ShowSensitiveRiskDialogAsync(target, _life.Token))) return;
            if (_closed || _life.IsCancellationRequested || !_rulesReadable) return;

            var latest = await Task.Run(_backend.Capture, _life.Token);
            if (_closed || _life.IsCancellationRequested || !_rulesReadable) return;
            var decision = _backend.Policy.EvaluateSensitiveClose(target, latest, RunningRules);
            if (decision.Protected) { Notice(L.T("敏感进程仍受保护") + " | " + decision.Reason); Log(decision.Reason); return; }
            ProtectionDecision Recheck(ProcessRecord candidate)
            {
                if (_closed || _life.IsCancellationRequested || !_rulesReadable || candidate.Id != target.Id || candidate.StartTimeUtcTicks != target.StartTimeUtcTicks ||
                    candidate.SessionId != target.SessionId || candidate.OwnerSid != target.OwnerSid ||
                    !SamePath(candidate.Path, target.Path) || !string.Equals(candidate.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                    return new ProtectionDecision(true, L.T("敏感进程仍受保护"));
                return _backend.Policy.EvaluateSensitiveClose(target, _backend.Capture(), RunningRules);
            }
            Log(L.F($"用户确认敏感进程关闭 | {target.Name} | PID {target.Id}"));
            var progress = new Progress<string>(Log);
            var result = _backend.ExecuteSensitiveClose is not null
                ? await _backend.ExecuteSensitiveClose(target, latest, Recheck, _life.Token)
                : await Task.Run(() => new ProcessCloser(capture: _backend.Capture).CloseDetailedAsync(new[] { target }, true, Recheck, latest, _life.Token, progress));
            foreach (var item in result.Results) Log(L.T("敏感进程关闭结果") + " | " + item.ProcessName + " | PID " + item.ProcessId + " | " + item.Outcome);
            Notice(L.T("敏感进程关闭结果"), result.Results.Count > 0 && result.Results.All(item => item.Success));
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Notice(exception.Message); Log(exception.Message); }
        finally { _busy = false; if (!_closed) { await CaptureAsync(); await RenderAsync(); } }
    }

    private async Task<bool> ShowSensitiveRiskDialogAsync(ProcessRecord target, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _closed) return false;
        var area = _backend.WorkingArea();
        var dialog = new Window
        {
            Owner = this, Title = "Process Keeper | " + L.T("敏感操作 | 二次确认"), Width = Math.Min(620, Math.Max(1, area.Width - 16)),
            MaxHeight = Math.Max(1, area.Height - 16), SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Resources["PageBrush"], Foreground = (Brush)Resources["InkBrush"]
        };
        dialog.Resources["PanelBrush"] = Resources["PanelBrush"]; dialog.Resources["InkBrush"] = Resources["InkBrush"];
        var root = new Grid { Margin = new Thickness(24), Background = dialog.Background };
        root.RowDefinitions.Add(new RowDefinition()); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(Text(L.T("敏感操作 | 二次确认"), 24));
        content.Children.Add(Text(target.Name + " | PID " + target.Id + "\n" + target.Path));
        content.Children.Add(Text(L.T("关闭桌面、开始菜单或输入组件可能导致桌面、任务栏或输入功能暂时不可用，并中断正在进行的任务或丢失未保存的数据。")));
        content.Children.Add(Text(L.T("风险与免责说明"), 19));
        content.Children.Add(Text(L.T("此操作由你主动选择。请先保存工作；Process Keeper 无法保证界面自动恢复，也无法找回丢失的数据。")));
        var acknowledge = new CheckBox
        {
            Tag = "sensitive-acknowledge", IsChecked = false, Margin = new Thickness(0, 4, 0, 16),
            Content = new TextBlock { Text = L.T("我已了解上述风险并确认继续"), TextWrapping = TextWrapping.Wrap }
        };
        content.Children.Add(acknowledge);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; Grid.SetRow(actions, 1); root.Children.Add(actions);
        var cancel = new Button { Content = L.T("保留运行"), Tag = "sensitive-cancel", Style = (Style)Resources[typeof(Button)], IsCancel = true, IsDefault = true };
        var confirm = new Button { Content = L.F($"确认强制关闭（{5} 秒）"), Tag = "sensitive-confirm", Style = (Style)Resources[typeof(Button)], IsEnabled = false };
        actions.Children.Add(cancel); actions.Children.Add(confirm); dialog.Content = root;
        var elapsed = new Stopwatch(); var started = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        bool Eligible() => started && elapsed.Elapsed >= TimeSpan.FromSeconds(5) && acknowledge.IsChecked == true && !cancellationToken.IsCancellationRequested && !_closed;
        void Update()
        {
            var remaining = started ? Math.Max(0, (int)Math.Ceiling(5 - elapsed.Elapsed.TotalSeconds)) : 5;
            confirm.Content = remaining > 0 ? L.F($"确认强制关闭（{remaining} 秒）") : L.T("确认强制关闭");
            confirm.IsEnabled = Eligible();
        }
        dialog.ContentRendered += (_, _) => { if (started) return; started = true; elapsed.Start(); timer.Start(); Update(); };
        acknowledge.Checked += (_, _) => Update(); acknowledge.Unchecked += (_, _) => Update();
        timer.Tick += (_, _) => Update();
        cancel.Click += (_, _) => dialog.DialogResult = false;
        confirm.Click += (_, _) => { if (Eligible()) dialog.DialogResult = true; };
        dialog.Closed += (_, _) => timer.Stop();
        using var cancellation = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (dialog.IsVisible) dialog.DialogResult = false; })));
        try
        {
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested || _closed) return false;
            return dialog.ShowDialog() == true && Eligible();
        }
        finally { timer.Stop(); elapsed.Stop(); }
    }
}
