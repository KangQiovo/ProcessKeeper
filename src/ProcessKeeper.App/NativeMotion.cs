using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace ProcessKeeper.App;

/// <summary>Animates a whole view with native XAML timelines, without animating list rows.</summary>
internal static class NativeMotion
{
    internal const int DurationMilliseconds = 320;
    internal static bool IsEnabled
    {
        get
        {
            try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled &&
                !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast; }
            catch { return false; }
        }
    }

    internal static async Task AnimateAsync(FrameworkElement element, double from, double to,
        double offsetFrom, double offsetTo, bool enabled, CancellationToken cancellationToken = default,
        int milliseconds = DurationMilliseconds)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled || !element.IsLoaded) { element.Opacity = to; return; }
        var originalOpacity = element.Opacity;
        var originalTransform = element.RenderTransform;
        var movement = new TranslateTransform();
        var transforms = new TransformGroup();
        if (originalTransform is not null) transforms.Children.Add(originalTransform);
        transforms.Children.Add(movement);
        var timeline = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacity = new DoubleAnimation { From = from, To = to, Duration = duration, EasingFunction = ease };
        var displacement = new DoubleAnimation { From = offsetFrom, To = offsetTo, Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(opacity, element); Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(displacement, movement); Storyboard.SetTargetProperty(displacement, "Y");
        timeline.Children.Add(opacity); timeline.Children.Add(displacement);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        timeline.Completed += (_, _) => completed.TrySetResult(true);
        using var registration = cancellationToken.Register(() => completed.TrySetCanceled(cancellationToken));
        element.RenderTransform = transforms;
        try
        {
            timeline.Begin();
            await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds + 1200), cancellationToken);
            timeline.Stop();
            element.Opacity = to;
        }
        catch
        {
            timeline.Stop();
            element.Opacity = originalOpacity;
            throw;
        }
        finally { element.RenderTransform = originalTransform; }
    }
}
