using System.Windows.Controls;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private PerformanceView? _performanceView;
    private string _performanceLanguage = "";

    private void EnsurePerformance()
    {
        if (_performanceView is not null && _performanceLanguage == L.Language) return;
        var preferences = _performanceView?.CapturePreferences();
        var saves = _performanceView?.SaveQueue;
        _performanceView?.Dispose();
        _performanceView = new PerformanceView(this, Root, _directory, saveQueue: saves, initialPreferences: preferences);
        _performanceLanguage = L.Language;
    }
    private void AddPerformanceView(Panel panel)
    {
        try
        {
            EnsurePerformance();
            if (_performanceView!.Parent is Panel previous) previous.Children.Remove(_performanceView);
            panel.Children.Add(_performanceView);
        }
        catch (Exception ex) { panel.Children.Add(Text(L.T("性能显示") + " | " + ex.Message)); }
    }
    internal bool HasPendingPerformanceSave => _performanceView?.HasPendingSave == true;
    internal Task FlushPerformanceAsync() => _performanceView?.FlushPendingAsync() ?? Task.CompletedTask;
    internal bool TryDiscardFailedPerformanceSave() => _performanceView?.DiscardSaveFailure() ?? true;
    private PerformancePreferences PerformanceForExport() => _performanceView?.CapturePreferences() ?? new PerformancePreferencesStore(_directory).Load();
}
