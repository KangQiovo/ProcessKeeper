using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

/// <summary>Identity-checked native primitives. No operation selects an emulator implicitly.</summary>
public static partial class AvdNative
{
    private static readonly HashSet<string> AndroidEnvironmentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ANDROID_AVD_HOME", "ANDROID_USER_HOME", "ANDROID_EMULATOR_HOME", "ANDROID_SDK_HOME",
        "ANDROID_HOME", "ANDROID_SDK_ROOT", "HOME", "USERPROFILE", "TEMP", "TMP"
    };

    public static void ValidateIdentity(ProcessRecord process)
    {
        using var handle = OpenVerified(process);
    }

    public static IReadOnlyList<string> ReadArguments(ProcessRecord process)
    {
        using var handle = OpenVerified(process);
        int needed = 0;
        ProcessNative.NtQueryInformationProcess(handle, 60 /* ProcessCommandLineInformation */, 0, 0, out needed);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (needed < 16 || needed > 1024 * 1024) throw new InvalidOperationException(L.T("进程命令行大小不可核实。"));
            int capacity = needed;
            nint buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                int status = ProcessNative.NtQueryInformationProcess(handle, 60, buffer, capacity, out needed);
                if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023)) continue;
                if (status < 0) throw new InvalidOperationException(L.F($"无法读取进程命令行（NTSTATUS 0x{status:X8}）。"));
                int length = (ushort)Marshal.ReadInt16(buffer);
                nint text = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
                long offset = text.ToInt64() - buffer.ToInt64();
                if (length == 0 || (length & 1) != 0 || offset < 0 || offset > capacity - length)
                    throw new InvalidOperationException(L.T("进程命令行缓冲区无效。"));
                string commandLine = Marshal.PtrToStringUni(text, length / 2)!;
                if (commandLine.Contains('\0')) throw new InvalidOperationException(L.T("进程命令行包含无效终止符。"));
                nint arguments = ProcessNative.CommandLineToArgvW(commandLine, out int count);
                if (arguments == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("无法解析进程命令行。"));
                try
                {
                    if (count is < 1 or > 512) throw new InvalidOperationException(L.T("进程参数数量超出支持范围。"));
                    var result = new string[count];
                    for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size)) ?? "";
                    EnsureAlive(handle);
                    return result;
                }
                finally { ProcessNative.LocalFree(arguments); }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException(L.T("进程命令行在读取期间发生变化，请重新识别。"));
    }

    /// <summary>Reads only the allowed environment entries of the verified x64 process.
    /// Missing variables remain absent; the caller must not substitute its own environment.</summary>
    public static IReadOnlyDictionary<string, string> ReadAndroidEnvironment(ProcessRecord process)
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException(L.T("仅支持核实 x64 模拟器环境。"));
        using var handle = OpenVerified(process, readMemory: true);
        if (!ProcessNative.IsWow64Process(handle, out bool wow64) || wow64)
            throw new InvalidOperationException(L.T("无法核实模拟器的 x64 环境，未使用当前应用环境代替。"));
        nint basic = Marshal.AllocHGlobal(48);
        try
        {
            int status = ProcessNative.NtQueryInformationProcess(handle, 0 /* ProcessBasicInformation */, basic, 48, out _);
            if (status < 0) throw new InvalidOperationException(L.T("无法读取模拟器环境地址。"));
            nint peb = Marshal.ReadIntPtr(basic, 8);
            nint parameters = ReadPointer(handle, peb + 0x20);
            nint environment = ReadPointer(handle, parameters + 0x80);
            if (peb == 0 || parameters == 0 || environment == 0 || (environment.ToInt64() & 1) != 0)
                throw new InvalidOperationException(L.T("模拟器环境地址无效。"));
            var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var entry = new StringBuilder();
            bool terminatedEntry = false;
            long address = environment.ToInt64();
            const int limit = 1024 * 1024;
            for (int total = 0; total < limit;)
            {
                // Read to page boundaries so a terminator at the end of a committed
                // region does not require reading a following uncommitted page.
                int size = Math.Min(4096 - (int)(address & 4095), limit - total);
                var bytes = new byte[size];
                if (!ProcessNative.ReadProcessMemory(handle, new nint(address), bytes, (nuint)bytes.Length, out nuint read) || read != (nuint)bytes.Length)
                    throw new InvalidOperationException(L.T("模拟器环境不可读，未按当前应用环境猜测 AVD。"));
                for (int i = 0; i < bytes.Length; i += 2)
                {
                    char character = (char)(bytes[i] | (bytes[i + 1] << 8));
                    if (character == '\0')
                    {
                        if (terminatedEntry || entry.Length == 0)
                        {
                            EnsureAlive(handle);
                            if (ReadPointer(handle, parameters + 0x80) != environment)
                                throw new InvalidOperationException(L.T("模拟器环境在读取期间发生变化。"));
                            return selected;
                        }
                        string item = entry.ToString();
                        entry.Clear();
                        int equals = item.IndexOf('=');
                        if (equals > 0 && AndroidEnvironmentKeys.Contains(item[..equals]))
                        {
                            if (!selected.TryAdd(item[..equals], item[(equals + 1)..]))
                                throw new InvalidOperationException(L.T("模拟器环境存在重复的关键配置项。"));
                        }
                        terminatedEntry = true;
                    }
                    else
                    {
                        terminatedEntry = false;
                        entry.Append(character);
                        if (entry.Length > 65536) throw new InvalidOperationException(L.T("模拟器环境项超出支持范围。"));
                    }
                }
                address += size; total += size;
            }
            throw new InvalidOperationException(L.T("模拟器环境未在读取上限内正确终止。"));
        }
        finally { Marshal.FreeHGlobal(basic); }
    }

    private static nint ReadPointer(SafeProcessHandle handle, nint address)
    {
        if (address.ToInt64() <= 0) throw new InvalidOperationException(L.T("进程内存地址无效。"));
        byte[] value = new byte[IntPtr.Size];
        if (!ProcessNative.ReadProcessMemory(handle, address, value, (nuint)value.Length, out nuint read) || read != (nuint)value.Length)
            throw new InvalidOperationException(L.T("无法核实模拟器进程环境。"));
        return new nint(BitConverter.ToInt64(value));
    }

    private static SafeProcessHandle OpenVerified(ProcessRecord process, bool readMemory = false)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Id <= 4 || process.StartTimeUtcTicks <= 0 || process.SessionId < 0 || string.IsNullOrWhiteSpace(process.OwnerSid) ||
            !ProtectionPolicy.TryNormalizePath(process.Path, out string expectedPath))
            throw new InvalidOperationException(L.T("模拟器身份信息不完整，请刷新后重试。"));
        var handle = ProcessNative.OpenProcess(0x1000U | 0x00100000U | (readMemory ? 0x0010U : 0), false, process.Id);
        try
        {
            if (handle.IsInvalid) throw new InvalidOperationException(L.T("模拟器进程已退出或无法读取。"));
            EnsureAlive(handle);
            if (ProcessNative.GetProcessId(handle) != (uint)process.Id ||
                !ProcessNative.GetProcessTimes(handle, out var creation, out _, out _, out _) ||
                DateTime.FromFileTimeUtc(((long)creation.High << 32) | creation.Low).Ticks != process.StartTimeUtcTicks)
                throw new InvalidOperationException(L.T("模拟器 PID 或创建时间已变化。"));
            var path = new StringBuilder(32768); int capacity = path.Capacity;
            if (!ProcessNative.QueryFullProcessImageNameW(handle, 0, path, ref capacity) ||
                !ProtectionPolicy.TryNormalizePath(path.ToString(), out string actualPath) || !actualPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L.T("模拟器可执行文件路径无法核实。"));
            if (!ProcessNative.ProcessIdToSessionId((uint)process.Id, out uint targetSession) ||
                !ProcessNative.ProcessIdToSessionId((uint)Environment.ProcessId, out uint currentSession) ||
                targetSession != currentSession || targetSession != (uint)process.SessionId)
                throw new InvalidOperationException(L.T("模拟器不属于当前登录会话。"));
            if (!ProcessNative.OpenProcessToken(handle, 0x0008, out var token)) throw new InvalidOperationException(L.T("无法读取模拟器所属用户。"));
            using (token)
            {
                ProcessNative.GetTokenInformation(token, 1, 0, 0, out int size);
                if (size is < 8 or > 65536) throw new InvalidOperationException(L.T("模拟器账户信息无效。"));
                nint info = Marshal.AllocHGlobal(size);
                try
                {
                    if (!ProcessNative.GetTokenInformation(token, 1, info, size, out _)) throw new InvalidOperationException(L.T("无法核实模拟器账户。"));
                    string actualSid = new SecurityIdentifier(Marshal.ReadIntPtr(info)).Value;
                    using var current = WindowsIdentity.GetCurrent();
                    if (!actualSid.Equals(process.OwnerSid, StringComparison.Ordinal) || !actualSid.Equals(current.User?.Value, StringComparison.Ordinal))
                        throw new InvalidOperationException(L.T("模拟器不属于当前用户，未进行操作。"));
                }
                finally { Marshal.FreeHGlobal(info); }
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void EnsureAlive(SafeProcessHandle handle)
    {
        if (ProcessNative.WaitForSingleObject(handle, 0) != 0x00000102) throw new InvalidOperationException(L.T("模拟器进程已退出。"));
    }

    private static class ProcessNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileTime { public uint Low; public uint High; }
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("kernel32.dll")] internal static extern uint GetProcessId(SafeProcessHandle handle);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessTimes(SafeProcessHandle handle, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool QueryFullProcessImageNameW(SafeProcessHandle handle, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ProcessIdToSessionId(uint id, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ReadProcessMemory(SafeProcessHandle handle, nint address, [Out] byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWow64Process(SafeProcessHandle handle, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(SafeProcessHandle handle, uint access, out SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint buffer, int length, out int needed);
        [DllImport("ntdll.dll")] internal static extern int NtQueryInformationProcess(SafeProcessHandle handle, int kind, nint buffer, int length, out int needed);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CommandLineToArgvW(string commandLine, out int count);
        [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint memory);
    }
}
