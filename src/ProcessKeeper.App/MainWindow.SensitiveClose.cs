using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private async Task CloseSensitiveProcessAsync(ProcessRecord target)
    {
        if (_closed || _working || _dialogOpen || _windowOperationRunning || !_configurationHealthy || _autorunsView?.IsChanging == true) return;
        var decision = _policy.EvaluateSensitiveClose(target, _snapshot, RunningRules);
        if (decision.Protected)
        {
            ShowNotice(L.T("敏感进程仍受保护"), decision.Reason, InfoBarSeverity.Warning);
            return;
        }
        // This route owns exactly one frozen process identity; ordinary/bulk closing is unchanged.
        _working = true;
        _closeCancellation = new CancellationTokenSource();
        var token = _closeCancellation.Token;
        AppsList.IsEnabled = Navigation.IsEnabled = AddSelectedButton.IsEnabled = false;
        CloseOthersButton.IsEnabled = CloseSelectedButton.IsEnabled = false;
        try
        {
            var details = new TextBlock
            {
                Text = L.T(RiskConfirmationMode.IsEnabled ? "此操作仅针对下列进程。无视风险模式已跳过二次确认。" : "此操作仅针对下列进程。继续后还需确认风险。") + $"\n\n{target.ApplicationName} | {target.Name} | PID {target.Id}\n{target.Path}",
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 510
            };
            var confirmation = NewDialog(L.T("确认关闭敏感进程"), details, L.T("继续"));
            using (token.Register(() => confirmation.DispatcherQueue.TryEnqueue(() => confirmation.Hide())))
            {
                if (await ShowDialog(confirmation) != ContentDialogResult.Primary || _closed || token.IsCancellationRequested) return;
            }
            if (!RiskConfirmationMode.IsEnabled && !await SensitiveCloseConfirmation.ShowAsync(Root.XamlRoot, Root.ActualTheme, target, ShowDialog, token)) return;
            if (_closed || token.IsCancellationRequested) return;
            LoadingRing.IsActive = true;
            var latest = await Task.Run(_collector.Capture, token);
            if (_closed || token.IsCancellationRequested) return;
            var rules = RunningRules.ToArray();
            ProtectionDecision Recheck(ProcessRecord frozen) => _policy.EvaluateSensitiveClose(frozen, latest, rules);
            decision = Recheck(target);
            if (decision.Protected)
            {
                ShowNotice(L.T("敏感进程仍受保护"), decision.Reason, InfoBarSeverity.Warning);
                return;
            }
            Log(L.F($"用户确认敏感进程关闭 | {target.Name} | PID {target.Id}"));
            var report = await Task.Run(() => _closer.CloseDetailedAsync(new[] { target }, true, Recheck, latest, token), token);
            foreach (var result in report.Results) Log($"{result.ProcessName} | PID {result.ProcessId} | {result.Outcome}");
            if (!_closed) ShowNotice(L.T("敏感进程关闭结果"), string.Join("\n", report.Results.Select(result => result.Outcome)), report.Results.All(result => result.Success) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closed) { Log(L.T("关闭操作异常：") + exception.Message); ShowNotice(L.T("操作中止"), exception.Message, InfoBarSeverity.Error); }
        }
        finally
        {
            _closeCancellation?.Dispose(); _closeCancellation = null;
            _working = false;
            if (!_closed)
            {
                LoadingRing.IsActive = false;
                AppsList.IsEnabled = Navigation.IsEnabled = true;
                await RefreshAsync();
            }
        }
    }
}
