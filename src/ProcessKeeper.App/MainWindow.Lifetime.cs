using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private bool _savingBeforeTransition;
    private bool _closeApproved;
    private bool _closingMotion;
    private bool _replacementPreparing;
    private bool _closingIntent;

    internal async Task CloseForReplacementAsync()
    {
        if (_replacementPreparing || _closed) return;
        _replacementPreparing = true; _timer.Stop(); CancelInstalledWork();
        Root.IsHitTestVisible = false;
        if (_updatesView is not null) await _updatesView.CloseAndWaitAsync();
        if (_downloadView is not null) await _downloadView.CancelAndWaitAsync();
        if (_memoryView is not null) await _memoryView.StopAndWaitAsync();
        try { await FlushPerformanceAsync(); }
        catch { TryDiscardFailedPerformanceSave(); }
        _closeApproved = true; _replacementPreparing = false; _motionLifetime.Cancel();
        if (!_closed) Close();
    }

    private void InitializeClosePersistence()
    {
        AppWindow.Closing += CloseAfterPerformanceSave;
        InitializeWindowMotion();
    }

    private async void CloseAfterPerformanceSave(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_replacementPreparing) { args.Cancel = true; return; }
        if (_closeApproved) return;
        if (_savingBeforeTransition || _closingMotion) { args.Cancel = true; return; }
        args.Cancel = true;
        _closingIntent = true; _updatesView?.SuspendForClosing();
        _savingBeforeTransition = true;
        var toolsReady = false;
        try
        {
            toolsReady = await PrepareToolsForCloseAsync();
            if (toolsReady && _updatesView is not null) await _updatesView.CancelAndWaitAsync();
        }
        catch (Exception error) { if (!_closed) ShowNotice(L.T("无法退出"), error.Message, InfoBarSeverity.Error); }
        finally { _savingBeforeTransition = false; }
        if (!toolsReady) { ResumeCanceledClose(); return; }
        if (await PrepareWindowTransitionAsync()) { await CloseWithMotionAsync(); return; }
        if (_closed) return;
        _savingBeforeTransition = true;
        var discard = false;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                Title = L.T("保存失败"), Content = L.T("性能显示的最后调整尚未保存。可以取消后重试，或不保存并退出。"),
                PrimaryButtonText = L.T("不保存并退出"), CloseButtonText = L.T("取消"),
                DefaultButton = ContentDialogButton.Close
            };
            discard = await ShowDialog(dialog) == ContentDialogResult.Primary && !_closed && TryDiscardFailedPerformanceSave();
        }
        catch (Exception error) { if (!_closed) ShowNotice(L.T("保存失败"), error.Message, InfoBarSeverity.Error); }
        finally { _savingBeforeTransition = false; }
        if (discard && !_closed) await CloseWithMotionAsync();
        else if (!_closed) ResumeCanceledClose();
    }
    private void ResumeCanceledClose() { _closingIntent = false; _updatesView?.ResumeAfterCloseCancellation(); }

    private async Task CloseWithMotionAsync()
    {
        if (_closed || _closingMotion) return;
        _closingMotion = true;
        Root.IsHitTestVisible = false;
        if (_performanceView is not null) _performanceView.IsEnabled = false;
        await StopEntranceMotionAsync();
        try { await NativeMotion.AnimateAsync(Root, Root.Opacity, 0, 0, -8, MotionEnabled(), _motionLifetime.Token); }
        catch { if (!_closed) Root.Opacity = 1; }
        if (!_closed) { _closeApproved = true; Close(); }
    }

    internal async Task<bool> PrepareWindowTransitionAsync()
    {
        if (_savingBeforeTransition || _closingMotion || _closed) return false;
        _savingBeforeTransition = true;
        var enabled = Root.IsHitTestVisible;
        var performanceEnabled = _performanceView?.IsEnabled;
        Root.IsHitTestVisible = false;
        if (_performanceView is not null) _performanceView.IsEnabled = false;
        try { await FlushPerformanceAsync(); return !_closed; }
        catch (Exception error) { if (!_closed) ShowNotice(L.T("保存失败"), error.Message, InfoBarSeverity.Error); return false; }
        finally
        {
            _savingBeforeTransition = false;
            if (!_closed)
            {
                Root.IsHitTestVisible = enabled;
                if (_performanceView is not null && performanceEnabled.HasValue) _performanceView.IsEnabled = performanceEnabled.Value;
            }
        }
    }
}
