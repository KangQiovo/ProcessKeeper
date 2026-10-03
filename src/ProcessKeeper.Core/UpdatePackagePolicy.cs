using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public enum UpdatePackageTarget { Universal, Windows7Compat, Windows10x64, Windows10arm64, Unsupported }
public sealed record UpdateRuntimeIdentity(int Major, int Minor, int Build, ushort Machine, bool CompatibilityUi, int ServicePack = 1, UpdatePackageTarget PackageFlavor = UpdatePackageTarget.Unsupported);

/// <summary>Exact release asset names and native host architecture; pointer width never selects an ARM update.</summary>
public static class UpdatePackagePolicy
{
    public static string AssetName(string version, UpdatePackageTarget target) => "ProcessKeeper-v" + version + (target switch
    {
        UpdatePackageTarget.Windows7Compat => "-win7-x86-compat.exe", UpdatePackageTarget.Windows10x64 => "-win10-x86-x64.exe",
        UpdatePackageTarget.Windows10arm64 => "-win10-arm64.exe", _ => ".exe"
    });
    public static UpdatePackageTarget Identify(string name, string version)
    {
        if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) return UpdatePackageTarget.Unsupported;
        foreach (var target in new[] { UpdatePackageTarget.Windows7Compat, UpdatePackageTarget.Windows10x64, UpdatePackageTarget.Windows10arm64 })
            if (name.Equals(AssetName(version, target), StringComparison.Ordinal)) return target;
        // Historical universal releases retain their existing filename contract.
        return UpdateService.PortableAssetName(name) && name.IndexOf("-win", StringComparison.OrdinalIgnoreCase) < 0
            ? UpdatePackageTarget.Universal : UpdatePackageTarget.Unsupported;
    }
    public static UpdatePackageTarget Target(UpdateRuntimeIdentity runtime)
    {
        if (runtime.PackageFlavor != UpdatePackageTarget.Unsupported)
        {
            var host = runtime with { PackageFlavor = UpdatePackageTarget.Unsupported };
            var basic = Target(host);
            if (basic == UpdatePackageTarget.Unsupported) return basic;
            if (runtime.PackageFlavor == UpdatePackageTarget.Universal) return UpdatePackageTarget.Universal;
            if (runtime.PackageFlavor == UpdatePackageTarget.Windows10x64 && (basic == UpdatePackageTarget.Windows7Compat || basic == UpdatePackageTarget.Windows10x64)) return UpdatePackageTarget.Windows10x64;
            return basic == runtime.PackageFlavor ? basic : UpdatePackageTarget.Unsupported;
        }
        bool modern = runtime.Major > 10 || runtime.Major == 10 && runtime.Build >= 19041;
        if (runtime.Machine == 0xaa64) return modern && !runtime.CompatibilityUi ? UpdatePackageTarget.Windows10arm64 : UpdatePackageTarget.Unsupported;
        if (runtime.Machine != 0x14c && runtime.Machine != 0x8664 || runtime.Major < 6 || runtime.Major == 6 && runtime.Minor < 1 || runtime.Major == 6 && runtime.Minor == 1 && runtime.ServicePack < 1 || runtime.Major == 6 && runtime.Minor == 2)
            return UpdatePackageTarget.Unsupported;
        if (runtime.CompatibilityUi || !modern || runtime.Machine == 0x14c) return UpdatePackageTarget.Windows7Compat;
        return UpdatePackageTarget.Windows10x64;
    }
    public static bool Supports(UpdatePackageTarget package, UpdateRuntimeIdentity runtime) =>
        package != UpdatePackageTarget.Unsupported && (runtime.PackageFlavor != UpdatePackageTarget.Unsupported ? package == Target(runtime) :
            package == UpdatePackageTarget.Universal ? Target(runtime) != UpdatePackageTarget.Unsupported : package == Target(runtime));
    public static UpdateRuntimeIdentity Current()
    {
        var version = new NativeVersion { Size = (uint)Marshal.SizeOf(typeof(NativeVersion)), ServicePack = "" };
        if (RtlGetVersion(ref version) != 0) return new(0, 0, 0, 0, true);
        ushort machine = 0;
        try { if (IsWow64Process2(new IntPtr(-1), out _, out ushort native)) machine = native; }
        catch (EntryPointNotFoundException) { /* Windows 7/8.1 has no ARM64 emulation; use its native system query. */ }
        if (machine == 0)
        {
            GetNativeSystemInfo(out var system);
            machine = system.Architecture switch { 0 => 0x14c, 9 => 0x8664, 12 => 0xaa64, _ => (ushort)0 };
        }
#if NETFRAMEWORK
        const bool compatibility = true;
#else
        const bool compatibility = false;
#endif
        var flavor = LauncherContextReader.TryGetCurrent(out var context, out _) ? context!.PackageTarget : UpdatePackageTarget.Unsupported;
        return new((int)version.Major, (int)version.Minor, (int)version.Build, machine, compatibility, version.ServicePackMajor, flavor);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeVersion
    { public uint Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; public ushort ServicePackMajor, ServicePackMinor, Suite; public byte ProductType, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSystem
    { public ushort Architecture, Reserved; public uint PageSize; public IntPtr Minimum, Maximum; public UIntPtr ActiveMask; public uint Processors, ProcessorType, Granularity; public ushort ProcessorLevel, Revision; }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref NativeVersion version);
    [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out NativeSystem system);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
