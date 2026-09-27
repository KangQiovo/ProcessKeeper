using ProcessKeeper.Core;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ProcessKeeper.App;

internal sealed record MyDockServicePlan(string RawPath, string ExecutablePath);

internal static class MyDockService
{
    private const string ServiceName = "MyDock";

    public static MyDockServicePlan? TryGetPlan()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\MyDock");
            if (key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw) return null;
            string? executable = TryParseExecutablePath(raw);
            return executable is not null ? new(raw, executable) : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>Pure validation used by the confirmation plan, opened-service recheck and tests.</summary>
    internal static string? TryParseExecutablePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.IndexOfAny(['\r', '\n', '\0']) >= 0) return null;
        string text = Environment.ExpandEnvironmentVariables(raw).Trim();
        if (text.Length == 0) return null;
        bool startsQuoted = text[0] == '"';
        bool endsQuoted = text[^1] == '"';
        if (startsQuoted != endsQuoted || startsQuoted && text.Length < 3) return null;
        if (startsQuoted)
        {
            text = text[1..^1];
            if (!string.Equals(text, text.Trim(), StringComparison.Ordinal)) return null;
        }
        // Only one executable is eligible. Never accept arguments or incomplete quoting.
        if (text.Length < 4 || !char.IsAsciiLetter(text[0]) || text[1] != ':' || text[2] is not ('\\' or '/') ||
            text.AsSpan(2).Contains(':') || text.IndexOfAny(['"', '*', '?', '<', '>', '|', '\r', '\n', '\0']) >= 0) return null;
        try
        {
            string full = Path.GetFullPath(text.Replace('/', '\\'));
            if (!Path.IsPathFullyQualified(full) || !Path.GetFileName(full).Equals("MyDock.exe", StringComparison.OrdinalIgnoreCase)) return null;
            string? directory = Path.GetDirectoryName(full);
            if (directory is null || !Path.GetFileName(directory).Equals("MyDockFinder", StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static string Stop(MyDockServicePlan selected)
    {
        var current = TryGetPlan();
        if (current is null || !string.Equals(current.RawPath, selected.RawPath, StringComparison.Ordinal) ||
            !string.Equals(current.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return L.T("MyDock 服务：安装路径已改变或无法核实，已跳过。");

        nint manager = OpenSCManagerW(null, null, 0x0001 /* CONNECT */);
        if (manager == 0) return L.T("MyDock 服务：") + new Win32Exception(Marshal.GetLastWin32Error()).Message;
        nint service = 0;
        try
        {
            // QUERY_CONFIG | QUERY_STATUS | ENUMERATE_DEPENDENTS | STOP; no change-config rights.
            service = OpenServiceW(manager, ServiceName, 0x002D);
            if (service == 0) return L.T("MyDock 服务：无法打开，请以管理员权限运行。");
            if (!MatchesOpenedService(service, selected.ExecutablePath))
                return L.T("MyDock 服务：打开后的服务配置与确认路径不一致或无法核实，已跳过。");
            if (!QueryServiceStatus(service, out var status)) return L.T("MyDock 服务：无法核实运行状态，已跳过。");
            if (status.State == 1) return L.T("MyDock 服务：已经停止。");
            if (status.State == 3) return L.T("MyDock 服务：已经处于停止过程中。");
            if ((status.ControlsAccepted & 0x0001) == 0) return L.T("MyDock 服务：当前不接受停止请求，已跳过。");

            bool enumerated = EnumDependentServicesW(service, 1, 0, 0, out _, out uint returned);
            int error = enumerated ? 0 : Marshal.GetLastWin32Error();
            if (returned > 0 || !enumerated && error == 234) return L.T("MyDock 服务：有其他运行中的依赖服务，已跳过。");
            if (!enumerated) return L.T("MyDock 服务：无法核实依赖，已跳过。");
            // Recheck on the same service object immediately before the one authorized action.
            if (!MatchesOpenedService(service, selected.ExecutablePath))
                return L.T("MyDock 服务：服务配置发生变化，已跳过。");
            if (!ControlService(service, 1, out _)) return L.T("MyDock 服务：停止失败，") + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                if (QueryServiceStatus(service, out status) && status.State == 1) return L.T("MyDock 服务：已停止。");
                Thread.Sleep(200);
            }
            return L.T("MyDock 服务：已发送停止请求，但尚未确认停止。");
        }
        finally
        {
            if (service != 0) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    private static bool MatchesOpenedService(nint service, string expectedPath)
    {
        bool queried = QueryServiceConfigW(service, 0, 0, out uint needed);
        if (queried || Marshal.GetLastWin32Error() != 122 || needed < Marshal.SizeOf<ServiceConfig>() || needed > 65536) return false;
        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) return false;
            var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
            // This exception is exclusively an own-process Win32 service, never a driver/shared host.
            if ((config.ServiceType & 0x0030) != 0x0010 || (config.ServiceType & 0x000B) != 0) return false;
            string? actualPath = TryParseExecutablePath(Marshal.PtrToStringUni(config.BinaryPathName));
            return actualPath is not null && string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public uint Type, State, ControlsAccepted, ExitCode, SpecificExitCode, CheckPoint, WaitHint; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public nint BinaryPathName, LoadOrderGroup;
        public uint TagId;
        public nint Dependencies, ServiceStartName, DisplayName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(nint service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDependentServicesW(nint service, uint state, nint buffer, uint size, out uint needed, out uint count);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfigW(nint service, nint buffer, uint size, out uint needed);
}
