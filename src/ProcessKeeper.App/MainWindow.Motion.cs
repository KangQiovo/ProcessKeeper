using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ProcessKeeper.App;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource _motionLifetime = new();
    private CancellationTokenSource? _entranceMotion;
    private CancellationTokenSource? _pageMotion;
    private Task _entranceTask = Task.CompletedTask;
    private Task _pageTask = Task.CompletedTask;
    private int _pageGeneration;
    private bool _entranceShown;
    internal Func<bool> MotionEnabled { get; set; } = () => NativeMotion.IsEnabled;

    private void InitializeWindowMotion()
    {
        Root.Loaded += (_, _) =>
        {
            if (_entranceShown) return;
            _entranceShown = true;
            _entranceMotion = CancellationTokenSource.CreateLinkedTokenSource(_motionLifetime.Token);
            _entranceTask = RevealShellAsync(_entranceMotion.Token);
        };
        Navigation.SelectionChanged += RevealSelectedPage;
        Closed += (_, _) => { _motionLifetime.Cancel(); _entranceMotion?.Cancel(); _pageMotion?.Cancel(); };
    }

    private async Task RevealShellAsync(CancellationToken token)
    {
        try { await NativeMotion.AnimateAsync(Root, 0, 1, 16, 0, MotionEnabled(), token); }
        catch { if (!_closed && !_closingMotion) Root.Opacity = 1; }
    }

    private async Task StopEntranceMotionAsync()
    {
        _entranceMotion?.Cancel();
        _pageMotion?.Cancel();
        try { await _entranceTask; } catch { }
        try { await _pageTask; } catch { }
    }

    private async void RevealSelectedPage(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready || !_entranceShown || _closed || _closingMotion) return;
        var generation = ++_pageGeneration;
        _pageMotion?.Cancel();
        try { await _pageTask; } catch { }
        if (generation != _pageGeneration || _closed || _closingMotion) return;
        var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(_motionLifetime.Token);
        _pageMotion = tokenSource;
        _pageTask = RevealSelectedPageAsync(tokenSource);
        await _pageTask;
    }

    private async Task RevealSelectedPageAsync(CancellationTokenSource tokenSource)
    {
        // Animate only the page host. Virtualized applications/process rows retain their state.
        var enabled = PageContent.IsHitTestVisible;
        PageContent.IsHitTestVisible = false;
        try { await NativeMotion.AnimateAsync(PageContent, 0, 1, 12, 0, MotionEnabled(), tokenSource.Token); }
        catch { }
        finally
        {
            if (ReferenceEquals(_pageMotion, tokenSource))
            {
                _pageMotion = null;
                if (!_closed) { PageContent.Opacity = 1; PageContent.IsHitTestVisible = enabled; }
            }
            tokenSource.Dispose();
        }
    }
}
