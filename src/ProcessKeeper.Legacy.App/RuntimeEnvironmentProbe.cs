using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using ProcessKeeper.Core;
namespace ProcessKeeper.App;
public sealed record RuntimeEnvironmentReport(string Description, bool NecessaryConditionsPassed);
internal static class RuntimeEnvironmentProbe
{
    internal static RuntimeEnvironmentReport Capture()
    {
        var lines = new List<string>();
        var version = new VersionInfo { Size = Marshal.SizeOf(typeof(VersionInfo)), ServicePack = "" };
        var readVersion = RtlGetVersion(ref version) == 0;
        var operatingSystem = readVersion && (version.Major > 6 || version.Major == 6 && (version.Minor > 1 || version.Minor == 1 && version.Build >= 7601));
        lines.Add(readVersion ? $"Windows {version.Major}.{version.Minor}.{version.Build} {version.ServicePack}" : L.T("部分运行环境无法读取，可重新检测。"));
        lines.Add(L.F($"系统 {(Environment.Is64BitOperatingSystem ? "x64" : "x86")} | 程序 {(Environment.Is64BitProcess ? "x64" : "x86")}"));
        var release = 0;
        try { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"); release = key?.GetValue("Release") is int value ? value : 0; } catch { }
        lines.Add($".NET Framework | CLR {Environment.Version} | Release {release}");
        lines.Add(L.F($"CPU 逻辑处理器：{Environment.ProcessorCount}"));
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        lines.Add(GlobalMemoryStatusEx(ref memory) ? L.F($"内存：{memory.TotalPhysical / 1073741824d:0.0} GB | 可用 {memory.AvailablePhysical / 1073741824d:0.0} GB") : L.T("物理内存：无法读取"));
        using var identity = WindowsIdentity.GetCurrent(); var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        lines.Add(L.T(admin ? "管理员权限：已获得" : "管理员权限：未获得"));
        lines.Add(L.T("当前环境使用纯色背景。"));
        return new RuntimeEnvironmentReport(string.Join("\n", lines), operatingSystem && release >= 394802 && admin);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct VersionInfo { public int Size; public uint Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; public ushort ServicePackMajor, ServicePackMinor, Suite; public byte Product, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref VersionInfo version);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
