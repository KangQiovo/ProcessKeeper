using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace ProcessKeeper.App;

public sealed partial class StartupWindow
{
    private readonly CancellationTokenSource _motionLifetime = new();
    private readonly CancellationTokenSource _rootEntranceMotion = new();
    private Task _rootEntranceTask = Task.CompletedTask;
    private bool _closing, _startupCloseApproved;
    internal Func<bool> MotionEnabled { get; set; } = () => NativeMotion.IsEnabled;

    private void InitializeMotion()
    {
        StartupRoot.Loaded += (_, _) => _rootEntranceTask = RevealRootAsync();
        AppWindow.Closing += CloseWithMotion;
        Closed += (_, _) => { _motionLifetime.Cancel(); _rootEntranceMotion.Cancel(); };
    }

    private async Task RevealRootAsync()
    {
        try { await NativeMotion.AnimateAsync(StartupRoot, 0, 1, 16, 0, MotionEnabled(), _rootEntranceMotion.Token); }
        catch { if (!_closed && !_closing) StartupRoot.Opacity = 1; }
    }

    private async void CloseWithMotion(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_startupCloseApproved) return;
        args.Cancel = true;
        if (_closing || _closed) return;
        _closing = true;
        StartupRoot.IsHitTestVisible = false;
        _lifetime.Cancel();
        _rootEntranceMotion.Cancel();
        try { await _rootEntranceTask; } catch { }
        try { await NativeMotion.AnimateAsync(StartupRoot, StartupRoot.Opacity, 0, 0, -8, MotionEnabled(), _motionLifetime.Token); }
        catch { if (!_closed) StartupRoot.Opacity = 1; }
        if (!_closed) { _startupCloseApproved = true; Close(); }
    }

    private async Task ChangeStepAsync(bool forward)
    {
        if (_transitioning || _closed || _closing) return;
        _transitioning = true;
        NextButton.IsEnabled = BackButton.IsEnabled = false;
        IntroductionScroll.IsHitTestVisible = false;
        foreach (var choice in LanguageChoices) choice.IsEnabled = false;
        try
        {
            await NativeMotion.AnimateAsync(IntroductionScroll, 1, 0, 0, forward ? -8 : 8,
                MotionEnabled(), _lifetime.Token, 90);
            if (_closed || _closing) return;
            if (forward) _progress.Next(); else _progress.Back();
            RenderStep();
            IntroductionScroll.Opacity = 1;
            await NativeMotion.AnimateAsync(IntroductionScroll, 0, 1, forward ? 16 : -16, 0,
                MotionEnabled(), _lifetime.Token, 230);
        }
        catch (OperationCanceledException) when (_closed || _closing) { }
        catch { if (!_closed && !_closing) IntroductionScroll.Opacity = 1; }
        finally
        {
            _transitioning = false;
            if (!_closed && !_closing)
            {
                IntroductionScroll.Opacity = 1;
                IntroductionScroll.IsHitTestVisible = true;
                SetBusy(_busy);
            }
        }
    }
}
