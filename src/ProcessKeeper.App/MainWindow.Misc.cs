using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private MemoryOptimizationView? _memoryView;
    private ResourceDownloadView? _downloadView;
    private void InitializeMisc()
    {
        _memoryView = new MemoryOptimizationView(ConfirmToolAsync, CanStartTool, Log);
        _memoryView.BusyChanged += busy => { _working = busy; if (!busy && !_closed) { RenderApps(); RenderInstalled(); RenderRules(); } };
        MemoryHost.Content = _memoryView;
        _downloadView = new ResourceDownloadView(async () => (await new FolderPicker(AppWindow.Id).PickSingleFolderAsync())?.Path,
            ConfirmToolAsync, OpenRowLocation, Log, CanStartTool);
        DownloadHost.Content = _downloadView;
    }
    private bool CanStartTool() => !_closed && !_closingIntent && !_closingMotion && !_savingBeforeTransition && !_replacementPreparing && !_working && !_dialogOpen && !_windowOperationRunning &&
        _updatesView?.IsBusy != true && _uninstallView?.IsBusy != true && _autorunsView?.IsChanging != true;
    private async Task<bool> ConfirmToolAsync(string title, string details)
    {
        if (_closed || _dialogOpen) return false;
        var dialog = NewDialog(title, "", L.T("继续"));
        dialog.Content = new ScrollViewer { MaxHeight = 380, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap } };
        dialog.PrimaryButtonText = L.T("继续"); dialog.CloseButtonText = L.T("取消"); dialog.DefaultButton = ContentDialogButton.Close;
        return await ShowDialog(dialog) == ContentDialogResult.Primary && !_closed;
    }
    private async Task<bool> PrepareToolsForCloseAsync()
    {
        if (_downloadView?.HasPendingJob == true && !await ConfirmToolAsync(L.T("取消下载并退出？"), L.T("未完成的下载将取消，临时分段会清理。已完成文件不会删除。"))) return false;
        if (_downloadView is not null) await _downloadView.CancelAndWaitAsync();
        if (_memoryView is not null) await _memoryView.StopAndWaitAsync();
        return !_closed;
    }
    private bool HasPendingTool => _memoryView?.IsBusy == true || _downloadView?.HasPendingJob == true;
}
