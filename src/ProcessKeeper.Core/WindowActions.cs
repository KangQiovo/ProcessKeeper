using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Acts only on an existing desktop window with a verified process identity.</summary>
public static class WindowActions
{
    public static (bool Success, string Message) Activate(ProcessRecord process, WindowRecord window, string? applicationKey = null) =>
        Execute(process, window, WindowOperation.Activate, applicationKey);

    public static (bool Success, string Message) Minimize(ProcessRecord process, WindowRecord window, string? applicationKey = null) =>
        Execute(process, window, WindowOperation.Minimize, applicationKey);

    public static (bool Success, string Message) RevealHidden(ProcessRecord process, WindowRecord window, string? applicationKey = null) =>
        Execute(process, window, WindowOperation.RevealHidden, applicationKey);

    /// <summary>Use the application's real page contract, not any window inherited by its process group.</summary>
    public static bool IsCandidate(ProcessRecord process, WindowRecord window, bool includeHidden = false, string? applicationKey = null) =>
        IsCandidate(window, includeHidden) && (!AvdGuiMatcher.RequiresNativeGui(process, applicationKey) ||
            AvdGuiMatcher.IsExistingGuiWindow(window, includeHidden));

    /// <summary>Pure snapshot filter. Native state and identity are always checked again before acting.</summary>
    public static bool IsCandidate(WindowRecord window, bool includeHidden = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Handle == 0 || !window.IsTopLevel || window.IsCloaked) return false;
        return window.IsVisible || (includeHidden && IsRevealableMainWindow(window));
    }

    private static bool IsRevealableMainWindow(WindowRecord window) =>
        window.IsTopLevel && !window.IsToolWindow && window.HasUsableBounds &&
        (window.HasCaption || window.HasAppWindowStyle) && !IsHelperClass(window.ClassName);

    private static bool IsHelperClass(string name) => name.Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("#32768", StringComparison.OrdinalIgnoreCase) || name.Equals("IME", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase) || name.Equals("CicMarshalWndClass", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("OleMainThreadWndClass", StringComparison.OrdinalIgnoreCase) || name.Equals("DDEMLMom", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SysShadow", StringComparison.OrdinalIgnoreCase);

    private enum WindowOperation { Activate, Minimize, RevealHidden }

    private static (bool Success, string Message) Execute(ProcessRecord process, WindowRecord window, WindowOperation operation, string? applicationKey)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(window);
        if (process.Id <= 4 || window.Handle == 0 || process.StartTimeUtcTicks <= 0 ||
            !ProtectionPolicy.TryNormalizePath(process.Path, out string expectedPath))
            return (false, L.T("窗口或进程身份信息不完整，请刷新后重试。"));
        bool requireAvdGui = AvdGuiMatcher.RequiresNativeGui(process, applicationKey);
        if (requireAvdGui && !AvdGuiMatcher.IsExistingGuiWindow(window, includeHidden: operation == WindowOperation.RevealHidden))
            return (false, L.T("这不是 AVD 的原生设备页面；命令行、终端或辅助窗口不会作为打开成功。请刷新后使用原生窗口重启入口。"));

        try
        {
            using var handle = WindowNative.OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */ | 0x00100000 /* SYNCHRONIZE */, false, process.Id);
            if (handle.IsInvalid || WindowNative.WaitForSingleObject(handle, 0) != 0x00000102 /* WAIT_TIMEOUT */)
                return (false, L.T("进程已退出或无法读取，请刷新列表。"));
            if (WindowNative.GetProcessId(handle) != (uint)process.Id ||
                !WindowNative.GetProcessTimes(handle, out var created, out _, out _, out _) ||
                DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks != process.StartTimeUtcTicks)
                return (false, L.T("进程创建时间已变化或无法核验，未操作窗口。"));
            var actualPath = new StringBuilder(32768);
            int pathLength = actualPath.Capacity;
            if (!WindowNative.QueryFullProcessImageNameW(handle, 0, actualPath, ref pathLength) ||
                !ProtectionPolicy.TryNormalizePath(actualPath.ToString(), out string normalizedPath) ||
                !normalizedPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                return (false, L.T("进程完整路径已变化或无法核验，未操作窗口。"));
            if (!WindowNative.ProcessIdToSessionId((uint)process.Id, out uint targetSession) ||
                !WindowNative.ProcessIdToSessionId((uint)Environment.ProcessId, out uint ourSession) || targetSession != ourSession)
                return (false, L.T("窗口不属于当前登录会话，或无法确认会话。"));

            bool reveal = operation == WindowOperation.RevealHidden;
            if (!ValidateWindow(handle, process.Id, window.Handle, out string reason, reveal, requireAvdGui)) return (false, reason);
            if (reveal)
            {
                if (!WindowNative.IsWindowVisible(window.Handle) || WindowNative.IsIconic(window.Handle))
                {
                    int command = WindowNative.IsIconic(window.Handle) ? 9 /* SW_RESTORE */ : 5 /* SW_SHOW */;
                    if (!WindowNative.ShowWindowAsync(window.Handle, command))
                        return (false, L.T("Windows 未接受显示请求；该窗口可能已变化或不允许显示。"));
                    // During this wait the target is expected to remain hidden until its
                    // owning thread processes ShowWindowAsync. Do not reject it prematurely.
                    if (!Observe(handle, process.Id, window.Handle,
                        () => WindowNative.IsWindowVisible(window.Handle) && !WindowNative.IsIconic(window.Handle), allowHidden: true, requireAvdGui: requireAvdGui))
                        return (false, L.T("已发送显示请求，但尚未观察到可见窗口；程序可能没有响应或再次隐藏了窗口。"));
                }
                if (!ValidateWindow(handle, process.Id, window.Handle, out reason, requireAvdGui: requireAvdGui)) return (false, reason);
                WindowNative.SetForegroundWindow(window.Handle);
                if (Observe(handle, process.Id, window.Handle,
                    () => !WindowNative.IsIconic(window.Handle) && WindowNative.GetForegroundWindow() == window.Handle, requireAvdGui: requireAvdGui))
                    return (true, L.T("已显示隐藏的主窗口并切到前台。"));
                if (ValidateWindow(handle, process.Id, window.Handle, out _, requireAvdGui: requireAvdGui) && !WindowNative.IsIconic(window.Handle))
                    return (true, L.T("已显示窗口，但 Windows 未允许切换焦点或焦点已被其他窗口接管；可从任务栏选择。"));
                return (false, L.T("窗口一度显示，随后又被程序隐藏、最小化或关闭，请刷新状态。"));
            }
            if (operation == WindowOperation.Minimize)
            {
                if (WindowNative.IsIconic(window.Handle)) return (true, L.T("窗口已经最小化。"));
                if (!WindowNative.ShowWindowAsync(window.Handle, 6 /* SW_MINIMIZE */))
                    return (false, L.T("Windows 未接受最小化请求，窗口可能已变化或受权限限制。"));
                if (Observe(handle, process.Id, window.Handle, () => WindowNative.IsIconic(window.Handle), requireAvdGui: requireAvdGui))
                    return (true, L.T("已最小化窗口。"));
                return (false, L.T("已发送最小化请求，但尚未确认窗口最小化；程序可能未响应。"));
            }

            if (WindowNative.IsIconic(window.Handle))
            {
                if (!WindowNative.ShowWindowAsync(window.Handle, 9 /* SW_RESTORE */))
                    return (false, L.T("Windows 未接受还原请求，窗口可能已变化或受权限限制。"));
                if (!Observe(handle, process.Id, window.Handle, () => !WindowNative.IsIconic(window.Handle), requireAvdGui: requireAvdGui))
                    return (false, L.T("已发送还原请求，但尚未确认窗口恢复；请稍后重试。"));
            }
            // Recheck after restoration as the app may destroy, hide, or replace its original window.
            if (!ValidateWindow(handle, process.Id, window.Handle, out reason, requireAvdGui: requireAvdGui)) return (false, reason);
            bool accepted = WindowNative.SetForegroundWindow(window.Handle);
            if (Observe(handle, process.Id, window.Handle,
                () => !WindowNative.IsIconic(window.Handle) && WindowNative.GetForegroundWindow() == window.Handle, requireAvdGui: requireAvdGui))
                return (true, L.T("已将窗口切到前台。"));
            return accepted
                ? (false, L.T("已请求切到前台，但焦点未停留在所选窗口；程序可能有活动对话框。"))
                : (false, L.T("Windows 未允许切换前台焦点；窗口仍保留，可从任务栏手动选择。"));
        }
        catch (Exception ex)
        {
            return (false, L.T("窗口操作未完成：") + ex.Message);
        }
    }

    // Keep the UI wait bounded, and report observed state rather than claiming success from a queued request.
    private static bool Observe(SafeProcessHandle handle, int processId, nint window, Func<bool> state, bool allowHidden = false, bool requireAvdGui = false)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            if (!ValidateWindow(handle, processId, window, out _, allowHidden, requireAvdGui)) return false;
            if (state()) return true;
            Thread.Sleep(15);
        } while (watch.Elapsed < TimeSpan.FromMilliseconds(250));
        return ValidateWindow(handle, processId, window, out _, allowHidden, requireAvdGui) && state();
    }

    private static bool ValidateWindow(SafeProcessHandle process, int processId, nint window, out string reason, bool allowHidden = false, bool requireAvdGui = false)
    {
        reason = L.T("窗口已关闭或所属进程已变化，请刷新列表。");
        if (WindowNative.WaitForSingleObject(process, 0) != 0x00000102 || !WindowNative.IsWindow(window)) return false;
        WindowNative.GetWindowThreadProcessId(window, out uint owner);
        if (owner != (uint)processId) return false;
        if (WindowNative.GetAncestor(window, 2 /* GA_ROOT */) != window)
        { reason = L.T("所选句柄不是顶层窗口，未执行操作。"); return false; }
        if (!IsOnCurrentDesktop(window))
        { reason = L.T("窗口不属于当前桌面，未执行跨桌面操作。"); return false; }
        // Normal activation/minimization never forces a hidden helper or tray window open.
        if (!allowHidden && !WindowNative.IsWindowVisible(window))
        { reason = L.T("窗口当前隐藏；请使用单独的隐藏主窗口显示操作。"); return false; }
        if (WindowNative.DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out uint cloaked, sizeof(uint)) < 0)
        { reason = L.T("无法核实窗口所在桌面的显示状态，未执行操作。"); return false; }
        if (cloaked != 0)
        { reason = L.T("窗口处于其他虚拟桌面或被系统隐藏，未执行操作。"); return false; }
        if (allowHidden && !IsRevealableMainWindow(ReadWindowShape(window)))
        { reason = L.T("所选窗口不是可显示的隐藏主窗口（可能是工具、消息辅助窗口或没有有效窗口区域）。"); return false; }
        if (requireAvdGui && !AvdGuiMatcher.IsExistingGuiWindow(ReadWindowShape(window), allowHidden))
        { reason = L.T("未确认 AVD 原生设备页面；当前窗口是命令行、辅助窗口或页面状态已变化，未将其当作打开成功。"); return false; }
        return true;
    }

    private static WindowRecord ReadWindowShape(nint window)
    {
        long style = WindowNative.GetWindowLong(window, -16).ToInt64();
        long extendedStyle = WindowNative.GetWindowLong(window, -20).ToInt64();
        var className = new StringBuilder(256);
        WindowNative.GetClassNameW(window, className, className.Capacity);
        var title = new StringBuilder(1024);
        WindowNative.GetWindowTextW(window, title, title.Capacity);
        return new WindowRecord(window, title.ToString(), WindowNative.IsWindowVisible(window), WindowNative.IsIconic(window), false, false)
        {
            ClassName = className.ToString(),
            IsTopLevel = WindowNative.GetAncestor(window, 2) == window && (style & 0x40000000L) == 0 && WindowNative.GetParent(window) != new nint(-3),
            IsToolWindow = (extendedStyle & 0x80) != 0,
            HasUsableBounds = WindowNative.GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top,
            HasCaption = (style & 0x00C00000L) == 0x00C00000L,
            HasAppWindowStyle = (extendedStyle & 0x00040000L) != 0
        };
    }

    private static bool IsOnCurrentDesktop(nint window)
    {
        nint desktop = WindowNative.GetThreadDesktop(WindowNative.GetCurrentThreadId());
        if (desktop == 0) return false;
        bool found = false;
        WindowNative.EnumDesktopWindowProc callback = (candidate, _) =>
        {
            if (candidate != window) return true;
            found = true;
            return false;
        };
        WindowNative.EnumDesktopWindows(desktop, callback, 0);
        GC.KeepAlive(callback);
        // GetThreadDesktop returns a borrowed handle; it must not be closed here.
        return found;
    }

    private static class WindowNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low; public uint High; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        internal delegate bool EnumDesktopWindowProc(nint window, nint parameter);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll")] internal static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
        [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint threadId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDesktopWindows(nint desktop, EnumDesktopWindowProc callback, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr64(nint window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(nint window, int index);
        internal static nint GetWindowLong(nint window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindowAsync(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, int size);
    }
}
