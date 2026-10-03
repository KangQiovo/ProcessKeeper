using System.Windows;
using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private MemoryOptimizationView? _memoryView;
    private ResourceDownloadView? _downloadView;
    private string _miscLanguage = "";
    private bool _toolDialogOpen;
    private bool HasPendingTool => _memoryView?.IsBusy == true || _downloadView?.HasPendingJob == true;
    private void EnsureMisc()
    {
        if (_miscLanguage == L.Language && _memoryView is not null) return;
        _memoryView?.Dispose(); _downloadView?.Dispose();
        _miscLanguage = L.Language;
        var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
        StackPanel Tab(string title)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 14) };
            tabs.Items.Add(new TabItem { Header = L.T(title), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top,
                Content = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel } });
            return panel;
        }
        _memoryView = new MemoryOptimizationView(ConfirmToolAsync, CanStartTool, Log);
        _memoryView.BusyChanged += busy => { _busy = busy; if (!busy && !_closed) _ = RenderAsync(); };
        Tab("内存优化").Children.Add(_memoryView);
        _downloadView = new ResourceDownloadView(() =>
        {
            using var picker = new System.Windows.Forms.FolderBrowserDialog { Description = L.T("保存目录") };
            return Task.FromResult<string?>(picker.ShowDialog() == System.Windows.Forms.DialogResult.OK ? picker.SelectedPath : null);
        }, ConfirmToolAsync, path => OpenLocation(path), Log, CanStartTool);
        Tab("下载器").Children.Add(_downloadView);
        AddPerformanceView(Tab("性能显示")); MiscHost.Content = tabs;
    }
    private bool CanStartTool() => !_closed && !_busy && !_toolDialogOpen && !_updateDialogOpen && !_updateDownloading && !_shortcutBusy && _uninstallView?.IsBusy != true;
    private async Task<bool> ConfirmToolAsync(string title, string message)
    {
        if (_closed || _toolDialogOpen) return false;
        _toolDialogOpen = true;
        try { return await ConfirmUninstall(title, message, true); }
        finally { _toolDialogOpen = false; }
    }
    private async Task<bool> PrepareToolsForCloseAsync()
    {
        if (_downloadView?.HasPendingJob == true && !await ConfirmToolAsync(L.T("取消下载并退出？"), L.T("未完成的下载将取消，临时分段会清理。已完成文件不会删除。"))) return false;
        if (_downloadView is not null) await _downloadView.CancelAndWaitAsync();
        if (_memoryView is not null) await _memoryView.StopAndWaitAsync();
        return !_closed;
    }
}
