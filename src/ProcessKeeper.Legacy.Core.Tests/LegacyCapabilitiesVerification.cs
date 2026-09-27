using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcessKeeper.Core;

internal static class LegacyCapabilitiesVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var type = typeof(AutorunWindowsBackend).Assembly.GetType("ProcessKeeper.Core.LegacyWindowsCapabilities", true);
        object Invoke(string name, params object[] values)
        { return type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, values); }
        check((uint)Invoke("QueryAccessFor", new Version(6, 1)) == 0x400, "Win7 uses full query permission for the critical-state probe");
        check((uint)Invoke("QueryAccessFor", new Version(6, 2)) == 0x400, "Win8 uses full query permission for the critical-state probe");
        check((uint)Invoke("QueryAccessFor", new Version(6, 3)) == 0x1000, "Win8.1 uses limited query permission for the native probe");
        check(!(bool)Invoke("HasPackageIdentity", new Version(6, 1)) && (bool)Invoke("HasPackageIdentity", new Version(6, 2)), "package identity follows its Win8 boundary");
        check(!(bool)Invoke("HasWindowCloaking", new Version(6, 1)) && (bool)Invoke("HasWindowCloaking", new Version(6, 2)), "window cloaking follows its Win8 boundary");
        var version = (Version)type.GetProperty("SystemVersion", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        check(version.Major >= 6, "RtlGetVersion returns a known current host");
        using (var invalid = new SafeProcessHandle(IntPtr.Zero, false))
        {
            var args = new object[] { invalid, false };
            check(!(bool)Invoke("IsProcessCritical", args) && (bool)args[1], "invalid critical-state query fails closed");
            args = new object[] { invalid, false };
            check(!(bool)Invoke("QueryBreakOnTermination", args) && (bool)args[1], "invalid legacy critical-state query fails closed");
        }
        using (var self = Process.GetCurrentProcess())
        using (var process = OpenProcess(0x400, false, self.Id))
        {
            check(!process.IsInvalid, "fixture opens only its own process with query rights");
            var legacy = new object[] { process, true };
            check((bool)Invoke("QueryBreakOnTermination", legacy) && !(bool)legacy[1], "same-handle legacy critical probe is read-only and succeeds");
            var native = new object[] { process, true };
            check((bool)Invoke("IsProcessCritical", native) && (bool)native[1] == (bool)legacy[1], "native critical result agrees with the legacy same-handle probe");
            var package = new object[] { process, (uint)0, null! };
            check((int)Invoke("GetPackageFamilyName", package) == 15700, "unpackaged fixture has no package identity");
            check(!process.IsClosed, "capability checks retain the caller's SafeHandle");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
}
