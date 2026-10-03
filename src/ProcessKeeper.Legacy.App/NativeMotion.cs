using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ProcessKeeper.App;

/// <summary>Uses WPF timelines for a whole view, with no per-row work or blocking sleeps.</summary>
internal static class NativeMotion
{
    internal const int DurationMilliseconds = 320;
    internal static bool IsEnabled => SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

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
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opacity = new DoubleAnimation(from, to, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var displacement = new DoubleAnimation(offsetFrom, offsetTo, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        opacity.Completed += (_, _) => completed.TrySetResult(true);
        using var registration = cancellationToken.Register(() => completed.TrySetCanceled());
        element.RenderTransform = transforms;
        try
        {
            movement.BeginAnimation(TranslateTransform.YProperty, displacement);
            element.BeginAnimation(UIElement.OpacityProperty, opacity);
            var timeout = Task.Delay(milliseconds + 1200, cancellationToken);
            if (await Task.WhenAny(completed.Task, timeout) != completed.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("The view transition did not complete.");
            }
            await completed.Task;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
        }
        catch
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = originalOpacity;
            throw;
        }
        finally { movement.BeginAnimation(TranslateTransform.YProperty, null); element.RenderTransform = originalTransform; }
    }
}
