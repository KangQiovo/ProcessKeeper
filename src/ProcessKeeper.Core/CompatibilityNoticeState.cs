namespace ProcessKeeper.Core;

public static class CompatibilityNoticeState
{
    public static bool Dismissed { get; set; }
    public static bool ShouldShow(bool compatibilityRuntime = false) => !Dismissed &&
        (compatibilityRuntime || !Environment.Is64BitProcess || Environment.OSVersion.Version.Major < 10 ||
         Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build < 22000);
}
