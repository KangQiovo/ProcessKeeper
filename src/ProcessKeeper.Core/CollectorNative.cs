using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

internal static class CollectorNative
{
    internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly long ToInt64() => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EnumServiceStatusProcess
    {
        public nint ServiceName;
        public nint DisplayName;
        public ServiceStatusProcess Status;
    }

    internal delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern KernelHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32FirstW(KernelHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool Process32NextW(KernelHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern KernelHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(KernelHandle process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(KernelHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessCritical(KernelHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool K32GetProcessMemoryInfo(KernelHandle process, ref ProcessMemoryCounters counters, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int GetPackageFamilyName(KernelHandle process, ref uint length, StringBuilder? name);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(KernelHandle process, uint access, out KernelHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(KernelHandle token, int informationClass, nint information, uint length, out uint returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(nint parent, EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr64(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(nint window, int index);
    internal static nint GetWindowLong(nint window, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint window, StringBuilder className, int capacity);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out uint value, uint size);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern ServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumServicesStatusExW(ServiceHandle manager, int infoLevel,
        uint serviceType, uint serviceState, nint services, uint bufferSize, out uint needed, out uint count, ref uint resume, string? groupName);
}
