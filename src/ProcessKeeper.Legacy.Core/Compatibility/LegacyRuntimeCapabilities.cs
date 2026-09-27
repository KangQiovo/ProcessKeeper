using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ProcessKeeper.Legacy.Core.Tests")]

namespace ProcessKeeper.Core;

public static class LegacyRuntimeCapabilities
{
    public static bool SupportsAvdRestart => IntPtr.Size == 8;
    public static bool SupportsBrowserLivePreview => false;
    public static bool SupportsPackageInventory => false;
    public static bool IsSupportedOperatingSystem => LegacyWindowsCapabilities.SystemVersion >= new Version(6, 1, 7601);
}
