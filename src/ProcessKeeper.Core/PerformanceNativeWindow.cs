using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public readonly record struct PerformanceRectangle(int X, int Y, int Width, int Height);
public static class PerformanceNativeWindow
{
    private static readonly SubclassProcedure FramelessProcedure = FramelessWindowProcedure;
    private static readonly UIntPtr FramelessId = new(0x504B5046);
    public static PerformanceRectangle Clamp(PerformanceRectangle requested, PerformanceRectangle work)
    {
        var width = Math.Max(1, Math.Min(requested.Width, work.Width)); var height = Math.Max(1, Math.Min(requested.Height, work.Height));
        return new PerformanceRectangle(Math.Max(work.X, Math.Min(requested.X, work.X + work.Width - width)),
            Math.Max(work.Y, Math.Min(requested.Y, work.Y + work.Height - height)), width, height);
    }
    public static PerformanceRectangle WorkArea(PerformanceRectangle bounds)
    {
        var rectangle = new Rect { Left = bounds.X, Top = bounds.Y, Right = bounds.X + bounds.Width, Bottom = bounds.Y + bounds.Height };
        var monitor = MonitorFromRect(ref rectangle, 2); var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new PerformanceRectangle(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }
    public static uint Dpi(IntPtr window)
    {
        try { var dpi = GetDpiForWindow(window); if (dpi > 0) return dpi; } catch (EntryPointNotFoundException) { }
        // Windows 7 uses system DPI; its user32 does not expose GetDpiForWindow.
        var dc = GetDC(window); if (dc == IntPtr.Zero) return 96;
        try { var dpi = GetDeviceCaps(dc, 88); return dpi > 0 ? (uint)dpi : 96; }
        finally { ReleaseDC(window, dc); }
    }
    public static bool Minimized(IntPtr window) => IsIconic(window);
    public static void Configure(IntPtr window, bool locked)
    {
        // WinUI's windowing controller may force WS_DLGFRAME back into its style
        // model. A native client calculation avoids that controller-owned frame.
        // The callback is rooted for the lifetime of the process and removes
        // itself when this owned overlay HWND is destroyed.
        EnsureFramelessClient(window);
        // Framework presenters can retain a resize/caption frame even without a
        // title bar. Strip that native chrome; the content region supplies corners.
        const long frameMask = 0x00C00000 | 0x00040000;
        var windowStyle = IntPtr.Size == 8 ? GetWindowLongPtr(window, -16).ToInt64() : GetWindowLong(window, -16);
        var frameless = windowStyle & ~frameMask;
        if (frameless != windowStyle)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr(window, -16, new IntPtr(frameless));
            else SetWindowLong(window, -16, (int)frameless);
            SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        const long transparent = 0x20, noActivate = 0x08000000, toolWindow = 0x80, exteriorEdges = 0x100 | 0x200 | 0x20000;
        var original = IntPtr.Size == 8 ? GetWindowLongPtr(window, -20).ToInt64() : GetWindowLong(window, -20);
        var style = original;
        style = (style | noActivate | toolWindow) & ~(transparent | exteriorEdges); if (locked) style |= transparent;
        if(style!=original){if (IntPtr.Size == 8) SetWindowLongPtr(window, -20, new IntPtr(style)); else SetWindowLong(window, -20, (int)style);}
        if(IsWindowEnabled(window)==locked)EnableWindow(window, !locked); // Block native caption input as well as framework hit testing.
    }
    private static void EnsureFramelessClient(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        try
        {
            if (!GetWindowSubclass(window, FramelessProcedure, FramelessId, out _) &&
                SetWindowSubclass(window, FramelessProcedure, FramelessId, UIntPtr.Zero))
                SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { /* A host without the v6 activation context still has the clipped-content fallback. */ }
    }
    private static IntPtr FramelessWindowProcedure(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        switch (message)
        {
            case 0x0083: // WM_NCCALCSIZE: retain the complete window rectangle as client content.
            case 0x0085: // WM_NCPAINT: no separate non-client border to paint.
                return IntPtr.Zero;
            case 0x0086: return new IntPtr(1); // WM_NCACTIVATE: no activation frame repaint.
            case 0x0084: return new IntPtr(1); // WM_NCHITTEST: no invisible resize edge.
            case 0x0082: // WM_NCDESTROY: never retain a callback on a recycled HWND.
                RemoveWindowSubclass(window, FramelessProcedure, FramelessId);
                break;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }
    public static void Position(IntPtr window, PerformanceRectangle bounds)
    {
        if(IsWindowVisible(window)&&Bounds(window)==bounds)return;
        SetWindowPos(window, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040);
    }
    public static PerformanceRectangle Bounds(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new PerformanceRectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    /// <summary>Use the system's anti-aliased corners when supported; otherwise clip
    /// the complete HWND to a DPI-scaled rounded region, including its backdrop.</summary>
    public static bool RoundCorners(IntPtr window, double radiusDip = 10, bool preferSystem = true)
    {
        if (window == IntPtr.Zero || !GetWindowRect(window, out var bounds)) return false;
        if (preferSystem)
        {
            try
            {
                // A pre-existing region opts a window out of DWM rounding.
                SetWindowRgn(window, IntPtr.Zero, true);
                var preference = 2; // DWMWCP_ROUND; DWMWA_WINDOW_CORNER_PREFERENCE = 33.
                if (DwmSetWindowAttribute(window, 33, ref preference, sizeof(int)) >= 0)
                {
                    var borderColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE.
                    DwmSetWindowAttribute(window, 34, ref borderColor, sizeof(int));
                    return true;
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var diameter = Math.Max(2, Math.Min(Math.Min(width, height), (int)Math.Round(Math.Max(1, radiusDip) * 2 * Dpi(window) / 96d)));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == IntPtr.Zero) return false;
        // On success Windows owns the HRGN. Only a failed transfer is ours to delete.
        if (SetWindowRgn(window, region, true) != 0) return true;
        DeleteObject(region);
        return false;
    }
    /// <summary>Clip the actual content with one uniform radius on every OS, including native backdrops.</summary>
    public static bool RoundContent(IntPtr window, PerformanceRectangle content, double radiusDip = 10)
    {
        if(window==IntPtr.Zero||content.Width<=0||content.Height<=0||!GetWindowRect(window,out var bounds))return false;
        try { var borderColor=unchecked((int)0xFFFFFFFE);DwmSetWindowAttribute(window,34,ref borderColor,sizeof(int)); } catch(Exception ex) when(ex is DllNotFoundException or EntryPointNotFoundException) { }
        // A pending layout can still describe the previous, larger content. Never
        // place a corner outside the actual HWND, and avoid overflow in its extent.
        var visibleLeft=Math.Max((long)bounds.Left,content.X);
        var visibleTop=Math.Max((long)bounds.Top,content.Y);
        var visibleRight=Math.Min((long)bounds.Right,(long)content.X+content.Width);
        var visibleBottom=Math.Min((long)bounds.Bottom,(long)content.Y+content.Height);
        if(visibleRight<=visibleLeft||visibleBottom<=visibleTop)return false;
        var left=(int)(visibleLeft-bounds.Left);var top=(int)(visibleTop-bounds.Top);
        var right=(int)(visibleRight-bounds.Left);var bottom=(int)(visibleBottom-bounds.Top);
        var diameter=Math.Max(2,Math.Min(Math.Min(right-left,bottom-top),(int)Math.Round(Math.Max(1,radiusDip)*2*Dpi(window)/96d)));
        // GDI excludes the bottom/right endpoint. The +1 keeps the region's
        // bounding box equal to the HWND and all four corner masks symmetric.
        var region=CreateRoundRectRgn(left,top,right+1,bottom+1,diameter,diameter);
        if(region==IntPtr.Zero)return false;
        if(SetWindowRgn(window,region,true)!=0)return true;
        DeleteObject(region);return false;
    }
    public static void Drag(IntPtr window) { ReleaseCapture(); SendMessage(window, 0x00A1, new IntPtr(2), IntPtr.Zero); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc,int index);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr window,bool enabled);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProcedure(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id, out UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProcedure callback, UIntPtr id);
    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
