using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using ProcessKeeper.Core;

internal sealed class OwnWindows : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Native.WindowProcedure _callback;
    private readonly Dictionary<string, nint> _windows = new();
    private readonly ManualResetEventSlim _delayStarted = new();
    private readonly string _className;
    private bool _disposed;
    internal IReadOnlyDictionary<string, nint> Handles => _windows;
    internal string ClassName => _className;

    internal OwnWindows(string? className = null)
    {
        _className = className ?? "ProcessKeeper.WindowRoutingFixture." + Guid.NewGuid().ToString("N");
        _callback = WindowProcedure;
        _thread = new Thread(Run) { IsBackground = true, Name = "Own fixture window message loop" };
        _thread.Start();
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }

    private void Run()
    {
        try
        {
            var registration = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(), Procedure = _callback,
                Instance = Native.GetModuleHandle(null), ClassName = _className
            };
            if (Native.RegisterClassEx(ref registration) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Add("main", 0, 0x00CF0000, 640, 420);
            Add("tool", 0x80, 0x00CF0000, 400, 250);
            Add("zero", 0, 0x80000000, 0, 0);
            Add("child", 0, 0x40000000, 200, 100, _windows["main"]);
            Add("stale", 0, 0x00CF0000, 350, 220);
            Native.DestroyWindow(_windows["stale"]);
            _ready.SetResult();
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            Native.UnregisterClass(_className, Native.GetModuleHandle(null));
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
    }

    private void Add(string name, uint extended, uint style, int width, int height, nint parent = 0)
    {
        var window = Native.CreateWindowEx(extended, _className, "Android Emulator | Fixture_AVD:5554 | Own fixture " + name, style,
            80, 80, width, height, parent, 0, Native.GetModuleHandle(null), 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create own " + name + " window");
        _windows.Add(name, window);
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x8001)
        {
            Native.ShowWindow(lParam, (int)wParam);
            return 0;
        }
        if (message == 0x8002)
        {
            foreach (var own in _windows.Values.Reverse()) if (Native.IsWindow(own)) Native.DestroyWindow(own);
            Native.PostQuitMessage(0);
            return 0;
        }
        if (message == 0x8003)
        {
            _delayStarted.Set();
            Thread.Sleep((int)wParam);
            return 0;
        }
        if (message == 0x0010) { Native.DestroyWindow(window); return 0; }
        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    internal void Hide(string name)
    {
        if (Native.SendMessageTimeout(_windows["main"], 0x8001, 0, _windows[name], 2, 2000, out _) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Own window loop did not process hide");
    }

    internal void DelayOwnMessageLoop(int milliseconds)
    {
        _delayStarted.Reset();
        if (!Native.PostMessage(_windows["main"], 0x8003, (nuint)milliseconds, 0) || !_delayStarted.Wait(2000))
            throw new Exception("Own message-loop delay did not begin");
    }

    internal WindowRecord Snapshot(string name)
    {
        var window = _windows[name];
        var text = new StringBuilder(512);
        Native.GetWindowText(window, text, text.Capacity);
        var className = new StringBuilder(256);
        Native.GetClassName(window, className, className.Capacity);
        Native.GetWindowRect(window, out var rectangle);
        var style = unchecked((ulong)Native.GetWindowLongPtr(window, -16).ToInt64());
        var extended = unchecked((ulong)Native.GetWindowLongPtr(window, -20).ToInt64());
        var cloaked = 0u;
        _ = Native.DwmGetWindowAttribute(window, 14, out cloaked, sizeof(uint));
        return new WindowRecord(window, text.ToString(), Native.IsWindowVisible(window), Native.IsIconic(window), cloaked != 0, Native.GetForegroundWindow() == window)
        {
            ClassName = className.ToString(), IsTopLevel = Native.GetAncestor(window, 2) == window,
            IsToolWindow = (extended & 0x80) != 0, HasUsableBounds = rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top,
            HasCaption = (style & 0x00C00000) == 0x00C00000, HasAppWindowStyle = (extended & 0x00040000) != 0
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_thread.IsAlive)
        {
            var target = _windows.Values.FirstOrDefault(Native.IsWindow);
            if (target != 0) Native.PostMessage(target, 0x8002, 0, 0);
            if (!_thread.Join(5000)) throw new TimeoutException("Own fixture window thread failed to stop");
        }
        GC.KeepAlive(_callback);
        _delayStarted.Dispose();
    }
}

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate nint WindowProcedure(nint window,uint message,nuint wParam,nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct WindowClass
    {
        internal uint Size, Style;
        internal WindowProcedure Procedure;
        internal int ClassExtra, WindowExtra;
        internal nint Instance, Icon, Cursor, Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct Message { internal nint Window; internal uint Id; internal nuint WParam; internal nint LParam; internal uint Time; internal Point Position; internal uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rectangle { internal int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] internal static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint="RegisterClassExW", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint="UnregisterClassW", CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string className,nint instance);
    [DllImport("user32.dll", EntryPoint="CreateWindowExW", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern nint CreateWindowEx(uint extended,string className,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll", EntryPoint="DefWindowProcW")] internal static extern nint DefWindowProc(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll", EntryPoint="GetMessageW")] internal static extern int GetMessage(out Message message,nint window,uint minimum,uint maximum);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint="DispatchMessageW")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", EntryPoint="PostMessageW")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window,uint message,nuint wParam,nint lParam);
    [DllImport("user32.dll", EntryPoint="SendMessageTimeoutW", SetLastError=true)] internal static extern nint SendMessageTimeout(nint window,uint message,nuint wParam,nint lParam,uint flags,uint timeout,out nuint result);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window,int command);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window,uint flags);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll", EntryPoint="GetWindowTextW", CharSet=CharSet.Unicode)] internal static extern int GetWindowText(nint window,StringBuilder text,int capacity);
    [DllImport("user32.dll", EntryPoint="SetWindowTextW", CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowText(nint window,string text);
    [DllImport("user32.dll", EntryPoint="GetClassNameW", CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint window,StringBuilder text,int capacity);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window,out Rectangle rectangle);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window,uint attribute,out uint value,int size);
}
