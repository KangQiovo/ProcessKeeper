using Microsoft.UI.Xaml.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private PerformanceView? _performanceView;

    private void InitializePerformance()
    {
        try
        {
            _performanceView = new PerformanceView(this, Root, Path.GetDirectoryName(_store.FilePath)!);
            PerformanceHost.Content = _performanceView;
        }
        catch (Exception ex) { ShowNotice(L.T("性能显示"), ex.Message, InfoBarSeverity.Warning); }
    }

    internal bool HasPendingPerformanceSave => _performanceView?.HasPendingSave == true;
    internal Task FlushPerformanceAsync() => _performanceView?.FlushPendingAsync() ?? Task.CompletedTask;
    internal bool TryDiscardFailedPerformanceSave() => _performanceView?.DiscardSaveFailure() ?? true;

    private PerformancePreferences PerformanceForExport() => _performanceView?.CapturePreferences() ??
        new PerformancePreferencesStore(Path.GetDirectoryName(_store.FilePath)!).Load();
}
