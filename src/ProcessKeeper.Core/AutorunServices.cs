using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public sealed partial class AutorunWindowsBackend
{
    internal sealed record ServicePayload(uint Type, uint Start, uint ErrorControl, string Binary, string Group, string Dependencies,
        string Account, string DisplayName, bool Delayed, uint Protected, string Triggers);

    private static AutorunState DisabledService(AutorunState state)
    {
        var value = JsonSerializer.Deserialize<ServicePayload>(state.Payload) ?? throw new InvalidDataException();
        return new(true, false, JsonSerializer.Serialize(value with { Start = 4 }));
    }
    internal static ServicePayload ServiceDetails(AutorunState state) => JsonSerializer.Deserialize<ServicePayload>(state.Payload) ?? throw new InvalidDataException();

    private static AutorunState ReadService(AutorunEntry entry)
    {
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, entry.Locator.ValueName, 1);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        QueryServiceConfig(service, 0, 0, out var size);
        if (size == 0 || size > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!QueryServiceConfig(service, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
            var payload = new ServicePayload(config.Type, config.Start, config.ErrorControl, ReadString(config.Binary), ReadString(config.Group),
                ReadMultiString(config.Dependencies), ReadString(config.Account), ReadString(config.DisplayName),
                ReadConfigScalar(service, 3) != 0, ReadConfigScalar(service, 12), ReadTriggers(service));
            return new(true, config.Start != 4, JsonSerializer.Serialize(payload));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool MutableService(ServicePayload service)
    {
        var target = ExtractTarget(service.Binary);
        if ((service.Type & 0x30) == 0 || (service.Type & 3) != 0 || service.Protected != 0 ||
            service.Start is not 2 and not 3 and not 4 || target.Length == 0 || IsSystemTarget(target) || !File.Exists(target)) return false;
        try { AutorunPathSafety.RejectReparseAncestors(target); return true; } catch { return false; }
    }

    private static void ApplyService(AutorunEntry entry, AutorunState expected, AutorunState desired)
    {
        var original = ServiceDetails(expected);
        var target = ServiceDetails(desired);
        if (!MutableService(original) || target.Start is not 2 and not 3 and not 4 ||
            original with { Start = target.Start } != target)
            throw new InvalidOperationException(L.T("服务的程序身份或其他配置已变化，未修改启动方式。"));
        using var manager = OpenSCManager(null, null, 1);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, entry.Locator.ValueName, 1 | 2);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (ReadService(entry).Fingerprint != expected.Fingerprint) throw new InvalidOperationException(L.T("入口已发生变化，请刷新列表后重新确认。"));
        if (!ChangeServiceConfig(service, uint.MaxValue, target.Start, uint.MaxValue, null, null, 0, null, null, null, null))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Start/stop controls are deliberately absent. Trigger data is never rewritten.
        // DelayedAutoStart is preserved by changing only the base start type.
    }

    private static uint ReadConfigScalar(ServiceHandle service, uint level)
    {
        var buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (!QueryServiceConfig2(service, level, buffer, 4, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadTriggers(ServiceHandle service)
    {
        QueryServiceConfig2(service, 8, 0, 0, out var size);
        if (size == 0) return "[]";
        if (size > 1024 * 1024) throw new InvalidDataException(L.T("服务触发配置超过读取上限。"));
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!QueryServiceConfig2(service, 8, buffer, size, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = Marshal.PtrToStructure<TriggerInfo>(buffer);
            if (info.Count > 128) throw new InvalidDataException();
            var triggers = new List<object>();
            for (var i = 0; i < info.Count; i++)
            {
                var trigger = Marshal.PtrToStructure<Trigger>(info.Triggers + i * Marshal.SizeOf<Trigger>());
                if (trigger.Count > 128) throw new InvalidDataException();
                var data = new List<object>();
                for (var j = 0; j < trigger.Count; j++)
                {
                    var item = Marshal.PtrToStructure<TriggerData>(trigger.Data + j * Marshal.SizeOf<TriggerData>());
                    if (item.Size > 256 * 1024) throw new InvalidDataException();
                    var bytes = new byte[checked((int)item.Size)]; if (bytes.Length > 0) Marshal.Copy(item.Data, bytes, 0, bytes.Length);
                    data.Add(new { item.Type, Content = Convert.ToBase64String(bytes) });
                }
                triggers.Add(new { trigger.Type, trigger.Action, Subtype = trigger.Subtype == 0 ? "" : Marshal.PtrToStructure<Guid>(trigger.Subtype).ToString(), Data = data });
            }
            return JsonSerializer.Serialize(triggers);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string ReadString(nint pointer) => pointer == 0 ? "" : Marshal.PtrToStringUni(pointer) ?? "";
    private static string ReadMultiString(nint pointer)
    {
        if (pointer == 0) return "";
        var result = new List<string>();
        for (var chars = 0; chars < 32768;)
        {
            var item = ReadString(pointer + chars * 2);
            if (item.Length == 0) break;
            result.Add(item); chars += item.Length + 1;
        }
        return string.Join('\0', result);
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfiguration
    { public uint Type, Start, ErrorControl; public nint Binary, Group; public uint Tag; public nint Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct TriggerInfo { public uint Count; public nint Triggers, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct Trigger { public uint Type, Action; public nint Subtype; public uint Count; public nint Data; }
    [StructLayout(LayoutKind.Sequential)] private struct TriggerData { public uint Type, Size; public nint Data; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    { private ServiceHandle() : base(true) { } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(ServiceHandle service, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig2(ServiceHandle service, uint level, nint buffer, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ChangeServiceConfig(ServiceHandle service, uint type, uint start, uint error, string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(nint service);
}
