using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace ProcessKeeper.App;

/// <summary>Keeps resizable windows usable while respecting the current monitor's work area.</summary>
internal static class WindowSizePolicy
{
    internal readonly record struct Limits(int MinimumWidth, int MinimumHeight, int MaximumWidth, int MaximumHeight);

    internal static Limits Calculate(RectInt32 workArea, uint dpi)
    {
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var margin = (int)Math.Round(16 * scale);
        var maximumWidth = Math.Max(1, workArea.Width - Math.Min(margin, Math.Max(0, workArea.Width - 1)));
        var maximumHeight = Math.Max(1, workArea.Height - Math.Min(margin, Math.Max(0, workArea.Height - 1)));
        return new Limits(Math.Min((int)Math.Round(900 * scale), maximumWidth),
            Math.Min((int)Math.Round(600 * scale), maximumHeight), maximumWidth, maximumHeight);
    }

    internal static void Attach(Window window)
    {
        var appWindow = window.AppWindow;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var applying = false;
        void Apply()
        {
            if (applying || appWindow.Presenter is not OverlappedPresenter presenter) return;
            applying = true;
            try
            {
                var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest)?.WorkArea;
                if (area is not { Width: > 0, Height: > 0 } workArea) return;
                var limits = Calculate(workArea, GetDpiForWindow(handle));
                // AppWindow dimensions are physical pixels; scale the logical minimum once.
                if (presenter.PreferredMinimumWidth != limits.MinimumWidth) presenter.PreferredMinimumWidth = limits.MinimumWidth;
                if (presenter.PreferredMinimumHeight != limits.MinimumHeight) presenter.PreferredMinimumHeight = limits.MinimumHeight;
                if (presenter.State != OverlappedPresenterState.Restored) return;
                var size = appWindow.Size;
                var width = Math.Clamp(size.Width, limits.MinimumWidth, limits.MaximumWidth);
                var height = Math.Clamp(size.Height, limits.MinimumHeight, limits.MaximumHeight);
                if (width == size.Width && height == size.Height) return;
                var position = appWindow.Position;
                appWindow.MoveAndResize(new RectInt32(
                    Math.Clamp(position.X, workArea.X, workArea.X + workArea.Width - width),
                    Math.Clamp(position.Y, workArea.Y, workArea.Y + workArea.Height - height), width, height));
            }
            finally { applying = false; }
        }
        void Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange) Apply();
        }
        appWindow.Changed += Changed;
        window.Activated += Activated;
        window.Closed += Closed;
        void Activated(object sender, WindowActivatedEventArgs args) => Apply();
        void Closed(object sender, WindowEventArgs args)
        {
            appWindow.Changed -= Changed;
            window.Activated -= Activated;
            window.Closed -= Closed;
        }
        Apply();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
