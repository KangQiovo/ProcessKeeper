using System.Windows;
using System.Windows.Controls;

namespace ProcessKeeper.App;

public partial class MainWindow
{
    private readonly CancellationTokenSource _motionLifetime = new();
    private CancellationTokenSource? _entranceMotion;
    private Task _entranceTask = Task.CompletedTask;
    private Task _pageTask = Task.CompletedTask;
    private CancellationTokenSource? _pageMotion;
    private int _pageGeneration;
    private bool _entranceShown;
    internal Func<bool> MotionEnabled { get; set; } = () => NativeMotion.IsEnabled;

    private void InitializeWindowMotion()
    {
        Navigation.SelectionChanged += RevealSelectedPage;
        Closed += (_, _) => { _motionLifetime.Cancel(); _entranceMotion?.Cancel(); _pageMotion?.Cancel(); };
    }

    private void RevealShell(object sender, RoutedEventArgs args)
    {
        if (_entranceShown) return;
        _entranceShown = true;
        _entranceMotion = CancellationTokenSource.CreateLinkedTokenSource(_motionLifetime.Token);
        _entranceTask = RevealShellAsync(_entranceMotion.Token);
    }

    private async Task RevealShellAsync(CancellationToken token)
    {
        try { await NativeMotion.AnimateAsync(Shell, 0, 1, 16, 0, MotionEnabled(), token); }
        catch { if (!_closed && !_closingMotion) Shell.Opacity = 1; }
    }

    private async Task StopEntranceMotionAsync()
    {
        _entranceMotion?.Cancel();
        _pageMotion?.Cancel();
        try { await _entranceTask; } catch { }
        try { await _pageTask; } catch { }
    }

    private async void RevealSelectedPage(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready || !_entranceShown || _closed || _closingMotion) return;
        var generation = ++_pageGeneration;
        _pageMotion?.Cancel();
        try { await _pageTask; } catch { }
        if (generation != _pageGeneration || _closed || _closingMotion) return;
        var target = Shell.Children.OfType<Grid>().FirstOrDefault(child => Grid.GetColumn(child) == 1);
        if (target is null) return;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_motionLifetime.Token);
        _pageMotion = source;
        _pageTask = RevealSelectedPageAsync(target, source);
        await _pageTask;
    }

    private async Task RevealSelectedPageAsync(FrameworkElement target, CancellationTokenSource source)
    {
        var enabled = target.IsEnabled;
        target.IsEnabled = false;
        try { await NativeMotion.AnimateAsync(target, 0, 1, 12, 0, MotionEnabled(), source.Token); }
        catch { }
        finally
        {
            if (ReferenceEquals(_pageMotion, source))
            {
                _pageMotion = null;
                if (!_closed) { target.Opacity = 1; target.IsEnabled = enabled; }
            }
            source.Dispose();
        }
    }
}
