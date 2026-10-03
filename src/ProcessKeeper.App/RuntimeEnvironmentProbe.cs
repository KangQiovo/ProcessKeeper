using System.Runtime.InteropServices;
using System.Security.Principal;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal sealed record RuntimeEnvironmentReport(string Windows, string OSArchitecture, string ProcessArchitecture, string Runtime,
    int LogicalProcessors, ulong? PhysicalMemory, ulong? AvailableMemory, bool Administrator, bool PlatformSupported = true)
{
    internal string Description => string.Join("\n", new[]
    {
        Windows + " | " + L.F($"系统 {OSArchitecture} | 程序 {ProcessArchitecture}"),
        Runtime,
        L.F($"CPU 逻辑处理器：{LogicalProcessors}") + " | " + (PhysicalMemory.HasValue
            ? L.F($"内存：{PhysicalMemory.Value / 1073741824d:0.0} GB | 可用 {AvailableMemory.GetValueOrDefault() / 1073741824d:0.0} GB")
            : L.T("物理内存：无法读取")),
        Administrator ? L.T("管理员权限：已获得") : L.T("管理员权限：未获得")
    });
}

internal static class RuntimeEnvironmentProbe
{
    internal static RuntimeEnvironmentReport Capture()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var memoryRead = GlobalMemoryStatusEx(ref memory);
        using var identity = WindowsIdentity.GetCurrent();
        var administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new(RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription, Environment.ProcessorCount,
            memoryRead ? memory.TotalPhysical : null, memoryRead ? memory.AvailablePhysical : null, administrator,
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            ProcessArchitecturePolicy.IsModernProcessArchitecture(RuntimeInformation.ProcessArchitecture.ToString()));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
