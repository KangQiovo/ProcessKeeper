using System.ComponentModel;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private bool _savingBeforeClose;
    private bool _closingForReplacement;
    private bool _closeApproved;
    private bool _closingMotion;

    private async void CloseAfterPerformanceSave(object? sender, CancelEventArgs args)
    {
        if (_closingForReplacement || _closeApproved) return;
        if (_savingBeforeClose || _closingMotion) { args.Cancel = true; return; }
        args.Cancel = true;
        _savingBeforeClose = true;
        var toolsReady = false;
        try { toolsReady = await PrepareToolsForCloseAsync(); }
        catch (Exception error) { if (!_closed) Notice(error.Message); }
        finally { _savingBeforeClose = false; }
        if (!toolsReady) return;
        _savingBeforeClose = true;
        var enabled = Root.IsEnabled;
        Root.IsEnabled = false;
        var saved = false;
        try { await FlushPerformanceAsync(); saved = true; }
        catch (Exception error) { if (!_closed) Notice(L.T("保存失败") + " | " + error.Message); }
        finally { _savingBeforeClose = false; if (!_closed) Root.IsEnabled = enabled; }
        if (saved) { await CloseWithMotionAsync(); return; }
        if (_closed) return;
        _savingBeforeClose = true;
        var discard = false;
        try
        {
            discard = await ConfirmDiscardPerformanceAsync() && !_closed && TryDiscardFailedPerformanceSave();
        }
        catch (Exception error) { if (!_closed) Notice(L.T("保存失败") + " | " + error.Message); }
        finally { _savingBeforeClose = false; }
        if (discard && !_closed) await CloseWithMotionAsync();
    }

    private async Task CloseWithMotionAsync()
    {
        if (_closed || _closingMotion) return;
        _closingMotion = true;
        Root.IsEnabled = false;
        await StopEntranceMotionAsync();
        try { await NativeMotion.AnimateAsync(Root, Root.Opacity, 0, 0, -8, MotionEnabled(), _motionLifetime.Token); }
        catch { if (!_closed) Root.Opacity = 1; }
        // Reduced-motion and already-flushed saves can finish synchronously inside
        // WPF's Closing event. Let that cancelled event return before closing again.
        await Task.Yield();
        if (!_closed) { _closeApproved = true; Close(); }
    }

    internal async Task CloseForReplacementAsync()
    {
        Root.IsEnabled = false;
        if (_downloadView is not null) await _downloadView.CancelAndWaitAsync();
        if (_memoryView is not null) await _memoryView.StopAndWaitAsync();
        try { await FlushPerformanceAsync(); }
        catch { TryDiscardFailedPerformanceSave(); }
        // Replacement is an existing explicit policy: a failed optional settings save
        // must not leave an older instance blocking the newest one behind a dialog.
        _closingForReplacement = true;
        _motionLifetime.Cancel();
        if (!_closed) Close();
    }

    private async Task<bool> ConfirmDiscardPerformanceAsync()
    {
        var message = L.T("性能显示的最后调整尚未保存。可以取消后重试，或不保存并退出。");
        if (_backend.Confirm is not null) return await _backend.Confirm(L.T("不保存并退出"), message);
        var background = System.Windows.SystemParameters.HighContrast ? System.Windows.SystemColors.WindowBrush
            : TryFindResource("PageBrush") as System.Windows.Media.Brush ?? System.Windows.SystemColors.WindowBrush;
        var foreground = System.Windows.SystemParameters.HighContrast ? System.Windows.SystemColors.WindowTextBrush
            : TryFindResource("InkBrush") as System.Windows.Media.Brush ?? System.Windows.SystemColors.WindowTextBrush;
        var buttonBackground = System.Windows.SystemParameters.HighContrast ? System.Windows.SystemColors.ControlBrush
            : TryFindResource("PanelBrush") as System.Windows.Media.Brush ?? System.Windows.SystemColors.ControlBrush;
        var dialog = new System.Windows.Window
        {
            Owner = this, Title = "Process Keeper | " + L.T("保存失败"), ShowInTaskbar = false,
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight, ResizeMode = System.Windows.ResizeMode.NoResize,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Background = background, Foreground = foreground
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(24), MaxWidth = 480 };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = System.Windows.TextWrapping.Wrap, Foreground = foreground });
        var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new System.Windows.Thickness(0, 20, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = L.T("取消"), IsCancel = true, IsDefault = true,
            MinWidth = 90, Padding = new System.Windows.Thickness(12, 7, 12, 7), Margin = new System.Windows.Thickness(0, 0, 12, 0), Background = buttonBackground, Foreground = foreground };
        var discard = new System.Windows.Controls.Button { Content = L.T("不保存并退出"), MinWidth = 120,
            Padding = new System.Windows.Thickness(12, 7, 12, 7), Background = buttonBackground, Foreground = foreground };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        discard.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(discard); panel.Children.Add(actions); dialog.Content = panel;
        dialog.Loaded += (_, _) => cancel.Focus();
        await Task.Yield();
        return !_closed && dialog.ShowDialog() == true;
    }
}
