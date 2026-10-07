using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

public enum UpdatePackageTarget { Universal, Windows7Compat, Windows10x64, Windows10arm64, Unsupported }
public sealed record UpdateRuntimeIdentity(int Major, int Minor, int Build, ushort Machine, bool CompatibilityUi, int ServicePack = 1,
    UpdatePackageTarget PackageFlavor = UpdatePackageTarget.Unsupported, UpdateDistributionKind DistributionKind = UpdateDistributionKind.Portable, int FrameworkRelease = 394802);

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
        if (IdentifyDistribution(name, version) == UpdateDistributionKind.Installer)
            name = name.Substring(0, name.Length - "-setup.exe".Length) + ".exe";
        else if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) return UpdatePackageTarget.Unsupported;
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
    public static UpdateDistributionKind IdentifyDistribution(string name, string version)
    {
        foreach (var target in new[] { UpdatePackageTarget.Windows7Compat, UpdatePackageTarget.Windows10x64, UpdatePackageTarget.Windows10arm64 })
        {
            var portable = AssetName(version, target);
            if (name.Equals(portable.Substring(0, portable.Length - ".exe".Length) + "-setup.exe", StringComparison.Ordinal)) return UpdateDistributionKind.Installer;
        }
        return name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) ? UpdateDistributionKind.Unknown :
            UpdateService.PortableAssetName(name) ? UpdateDistributionKind.Portable : UpdateDistributionKind.Unknown;
    }
    public static UpdateAsset? DefaultAsset(UpdateRelease release, UpdateRuntimeIdentity runtime)
    {
        if (runtime.DistributionKind == UpdateDistributionKind.Unknown || runtime.PackageFlavor == UpdatePackageTarget.Unsupported) return null;
        var target = runtime.PackageFlavor;
        var distribution = DefaultDistribution(runtime);
        var matches = release.Assets.Where(asset => asset.CanAutoInstall && asset.Restriction.Length == 0 && Supports(asset, runtime) &&
            asset.PackageTarget == target && asset.DistributionKind == distribution).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    public static bool IsPackageChange(UpdateAsset asset, UpdateRuntimeIdentity runtime) =>
        runtime.PackageFlavor == UpdatePackageTarget.Unsupported || runtime.DistributionKind == UpdateDistributionKind.Unknown ||
        asset.PackageTarget != runtime.PackageFlavor || asset.DistributionKind != DefaultDistribution(runtime);
    // Universal has no setup artifact. An installed Universal payload keeps its original
    // uninstall ownership while updating from the same Universal portable bundle.
    private static UpdateDistributionKind DefaultDistribution(UpdateRuntimeIdentity runtime) =>
        runtime.PackageFlavor == UpdatePackageTarget.Universal && runtime.DistributionKind == UpdateDistributionKind.Installer
            ? UpdateDistributionKind.Portable : runtime.DistributionKind;
    public static bool Supports(UpdatePackageTarget package, UpdateRuntimeIdentity runtime)
    {
        bool modern = runtime.Major > 10 || runtime.Major == 10 && runtime.Build >= 19041;
        if (runtime.Machine == 0xaa64) return modern && package is UpdatePackageTarget.Universal or UpdatePackageTarget.Windows10arm64;
        if (runtime.Machine != 0x14c && runtime.Machine != 0x8664 || runtime.Major < 6 || runtime.Major == 6 &&
            (runtime.Minor < 1 || runtime.Minor == 1 && runtime.ServicePack < 1 || runtime.Minor == 2)) return false;
        bool legacy = runtime.FrameworkRelease >= 394802;
        return package switch
        {
            UpdatePackageTarget.Windows7Compat => legacy,
            UpdatePackageTarget.Universal or UpdatePackageTarget.Windows10x64 => runtime.Machine == 0x8664 && modern || legacy,
            _ => false
        };
    }
    public static bool Supports(UpdateAsset asset, UpdateRuntimeIdentity runtime) => Supports(asset.PackageTarget, runtime) &&
        asset.DistributionKind is UpdateDistributionKind.Portable or UpdateDistributionKind.Installer &&
        (asset.DistributionKind != UpdateDistributionKind.Installer || asset.PackageTarget != UpdatePackageTarget.Universal &&
            (asset.PackageTarget != UpdatePackageTarget.Windows10x64 || runtime.Major >= 10));
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
        var hasContext = LauncherContextReader.TryGetCurrent(out var context, out _);
        var flavor = hasContext ? context!.PackageTarget : UpdatePackageTarget.Unsupported;
        int framework = 0;
        try { using var registry = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32);
            using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"); framework = key?.GetValue("Release") is int release ? release : 0; }
        catch { /* Unknown legacy runtime capability is never treated as installed. */ }
        return new((int)version.Major, (int)version.Minor, (int)version.Build, machine, compatibility, version.ServicePackMajor, flavor,
            hasContext ? context!.DistributionKind : UpdateDistributionKind.Unknown, framework);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NativeVersion
    { public uint Size, Major, Minor, Build, Platform; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ServicePack; public ushort ServicePackMajor, ServicePackMinor, Suite; public byte ProductType, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSystem
    { public ushort Architecture, Reserved; public uint PageSize; public IntPtr Minimum, Maximum; public UIntPtr ActiveMask; public uint Processors, ProcessorType, Granularity; public ushort ProcessorLevel, Revision; }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)] private static extern int RtlGetVersion(ref NativeVersion version);
    [DllImport("kernel32.dll")] private static extern void GetNativeSystemInfo(out NativeSystem system);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
