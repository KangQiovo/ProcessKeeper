#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcessKeeper.Core
{
    // APIs absent on Windows 7 are never called merely because the project compiled.
    // Unknown identity/critical-process results remain failures so callers refuse closure.
    internal static class LegacyWindowsCapabilities
    {
        private static readonly Version DetectedVersion = ReadVersion();
        internal static Version SystemVersion { get { return DetectedVersion; } }
        internal static uint ProcessQueryAccess { get { return QueryAccessFor(DetectedVersion); } }
        internal static bool IsServiceLaunchProtectionSupported
        {
            get
            {
                if (!Known(DetectedVersion)) throw new PlatformNotSupportedException();
                return AtLeast(DetectedVersion, 6, 3);
            }
        }
        internal static uint QueryAccessFor(Version version) { return AtLeast(version, 6, 3) ? 0x1000u : 0x0400u; }
        internal static bool HasPackageIdentity(Version version) { return AtLeast(version, 6, 2); }
        internal static bool HasWindowCloaking(Version version) { return AtLeast(version, 6, 2); }
        private static bool Known(Version version) { return AtLeast(version, 6, 1); }
        private static bool AtLeast(Version version, int major, int minor)
        { return version.Major > major || version.Major == major && version.Minor >= minor; }

        internal static bool IsProcessCritical(SafeHandle process, out bool critical)
        {
            critical = true;
            if (!Known(DetectedVersion) || process == null || process.IsClosed || process.IsInvalid) return false;
            if (AtLeast(DetectedVersion, 6, 3))
            {
                try
                {
                    bool value;
                    if (!NativeIsProcessCritical(process, out value)) return false;
                    critical = value; return true;
                }
                catch (EntryPointNotFoundException) { return false; }
                catch (DllNotFoundException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
            return QueryBreakOnTermination(process, out critical);
        }

        // Documented ProcessBreakOnTermination=29 is available before IsProcessCritical.
        // The caller opens PROCESS_QUERY_INFORMATION on Win7/8, then this exact SafeHandle
        // is also used for PID/start-time validation and any subsequent close operation.
        internal static bool QueryBreakOnTermination(SafeHandle process, out bool critical)
        {
            critical = true;
            if (process == null || process.IsClosed || process.IsInvalid) return false;
            var query = Resolve<NtQueryProcess>("ntdll.dll", "NtQueryInformationProcess");
            if (query == null) return false;
            var held = false;
            try
            {
                process.DangerousAddRef(ref held);
                uint value, returned;
                var status = query(process.DangerousGetHandle(), 29, out value, sizeof(uint), out returned);
                if (status < 0 || returned != sizeof(uint) || value > 1) return false;
                critical = value != 0; return true;
            }
            catch (ObjectDisposedException) { return false; }
            finally { if (held) process.DangerousRelease(); }
        }

        internal static int GetPackageFamilyName(SafeHandle process, ref uint length, StringBuilder? name)
        {
            if (!Known(DetectedVersion)) return 120; // ERROR_CALL_NOT_IMPLEMENTED, not "no package".
            if (process == null || process.IsClosed || process.IsInvalid) return 6;
            if (!HasPackageIdentity(DetectedVersion)) { length = 0; return 15700; }
            try { return NativeGetPackageFamilyName(process, ref length, name); }
            catch (EntryPointNotFoundException) { return 120; }
            catch (DllNotFoundException) { return 120; }
            catch (ObjectDisposedException) { return 6; }
        }

        internal static int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, int size)
        { return DwmGetWindowAttribute(window, attribute, out value, unchecked((uint)size)); }
        internal static int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size)
        {
            value = 0;
            if (!Known(DetectedVersion)) return unchecked((int)0x80004001);
            if (size != sizeof(uint) || window == IntPtr.Zero || !IsWindow(window)) return unchecked((int)0x80070057);
            if (attribute == 14 && !HasWindowCloaking(DetectedVersion)) return 0;
            try { return NativeDwmGetWindowAttribute(window, attribute, out value, size); }
            catch (EntryPointNotFoundException) { return unchecked((int)0x80004001); }
            catch (DllNotFoundException) { return unchecked((int)0x80004001); }
        }

        private static Version ReadVersion()
        {
            var query = Resolve<RtlVersion>("ntdll.dll", "RtlGetVersion");
            if (query == null) return new Version(0, 0);
            var data = new VersionInfo(); data.Size = (uint)Marshal.SizeOf(typeof(VersionInfo)); data.ServicePack = "";
            if (query(ref data) < 0 || data.Major > int.MaxValue || data.Minor > int.MaxValue || data.Build > int.MaxValue)
                return new Version(0, 0);
            return new Version((int)data.Major, (int)data.Minor, (int)data.Build);
        }

        private static T? Resolve<T>(string module, string name) where T : class
        {
            var library = GetModuleHandle(module);
            var address = library == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(library, name);
            return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct VersionInfo
        {
            internal uint Size, Major, Minor, Build, Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int RtlVersion(ref VersionInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int NtQueryProcess(IntPtr process, uint kind, out uint value, uint length, out uint returned);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", EntryPoint = "IsProcessCritical", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsProcessCritical(SafeHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
        [DllImport("kernel32.dll", EntryPoint = "GetPackageFamilyName", CharSet = CharSet.Unicode)]
        private static extern int NativeGetPackageFamilyName(SafeHandle process, ref uint length, StringBuilder? name);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int NativeDwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    }
}
