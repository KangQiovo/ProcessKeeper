using System.Runtime.InteropServices;

namespace ProcessKeeper.Core;

/// <summary>Architecture gates describe verified layouts, never infer an ISA from pointer width.</summary>
public static class ProcessArchitecturePolicy
{
    public static bool IsModernProcessArchitecture(string architecture) =>
        string.Equals(architecture, "X64", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(architecture, "Arm64", StringComparison.OrdinalIgnoreCase);

    public static bool CanReadX64ProcessEnvironment(int pointerSize, string processArchitecture, string systemArchitecture) =>
        pointerSize == 8 && string.Equals(processArchitecture, "X64", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(systemArchitecture, "X64", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsX64ProcessEnvironment
    {
        get
        {
#if NETFRAMEWORK
            // The distributed Framework route is x86. Do not enable an unverified
            // private PEB layout if somebody independently retargets that project.
            return false;
#else
            return CanReadX64ProcessEnvironment(IntPtr.Size, RuntimeInformation.ProcessArchitecture.ToString(), RuntimeInformation.OSArchitecture.ToString());
#endif
        }
    }
}
