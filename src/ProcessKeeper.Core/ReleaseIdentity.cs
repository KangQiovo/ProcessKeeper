namespace ProcessKeeper.Core;

/// <summary>The friendly revision label is presentation only; update and package identity use strict SemVer.</summary>
public static class ReleaseIdentity
{
    public const string NumericVersion = "1.8.1";
    public const string DisplayLabel = "1.8v2";
    public static string DisplayVersion(string version) =>
        UpdateVersion.TryParse(version, out var parsed) && parsed!.Value.Split('+')[0] == NumericVersion ? DisplayLabel : version;
}
