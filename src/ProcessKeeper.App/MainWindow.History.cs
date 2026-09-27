using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private double _historyManualOffset;
    private bool _historyScrollQueued, _historyRendering, _historyAdjusting;

    private void UpdateHistoryText()
    {
        if (_closed) return;
        if (!_historyScrollQueued) RememberHistoryOffset();
        _historyRendering = true;
        try { HistoryText.Text = _activityLog.Render(); }
        finally { _historyRendering = false; }
        QueueHistoryScroll();
    }

    private void HistoryAutoScrollChanged(object sender, RoutedEventArgs args)
    {
        if (_closed || HistoryScroll is null) return;
        if (!HistoryAutoScrollToggle.IsOn && HistoryPage.Visibility == Visibility.Visible)
            _historyManualOffset = HistoryScroll.VerticalOffset;
        QueueHistoryScroll();
        SaveViewPreferences();
    }

    private void HistoryViewportLoaded(object sender, RoutedEventArgs args) => QueueHistoryScroll();
    private void HistoryViewportSizeChanged(object sender, SizeChangedEventArgs args) => QueueHistoryScroll();

    private void HistoryViewportChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!_historyRendering && !_historyAdjusting && !_historyScrollQueued) RememberHistoryOffset();
    }

    private void RememberHistoryOffset()
    {
        if (HistoryScroll is not null && HistoryAutoScrollToggle is not null &&
            !HistoryAutoScrollToggle.IsOn && HistoryPage.Visibility == Visibility.Visible && HistoryScroll.ViewportHeight > 0)
            _historyManualOffset = HistoryScroll.VerticalOffset;
    }

    private void QueueHistoryScroll()
    {
        if (_closed || HistoryScroll is null || HistoryAutoScrollToggle is null || _historyScrollQueued ||
            HistoryPage.Visibility != Visibility.Visible || !HistoryScroll.IsLoaded) return;
        // Coalesce an entire batch of log messages. Measure the new text before choosing the bottom.
        _historyScrollQueued = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_closed || HistoryPage.Visibility != Visibility.Visible || !HistoryScroll.IsLoaded) return;
                HistoryScroll.UpdateLayout();
                var destination = HistoryAutoScrollToggle.IsOn ? HistoryScroll.ScrollableHeight :
                    Math.Clamp(_historyManualOffset, 0, HistoryScroll.ScrollableHeight);
                _historyAdjusting = true;
                try { HistoryScroll.ChangeView(null, destination, null, disableAnimation: true); }
                finally { _historyAdjusting = false; }
            }
            finally { _historyScrollQueued = false; }
        })) _historyScrollQueued = false;
    }
}
