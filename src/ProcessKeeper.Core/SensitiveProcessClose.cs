namespace ProcessKeeper.Core;

/// <summary>Recognition only, not permission. No process is opened, restarted or terminated here.</summary>
public static class SensitiveProcessClose
{
    // Exact, installed Windows paths only. Shared service hosts, DWM, authentication,
    // security software and service-backed search indexing intentionally have no exception.
    private static readonly string[] RelativePaths =
    [
        "explorer.exe",
        @"SystemApps\ShellExperienceHost_cw5n1h2txyewy\ShellExperienceHost.exe",
        @"SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe",
        @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe",
        @"SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe",
        @"SystemApps\InputApp_cw5n1h2txyewy\TextInputHost.exe",
        @"System32\ctfmon.exe"
    ];

    public static bool IsSensitiveComponent(ProcessRecord process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!ProtectionPolicy.TryNormalizePath(process.Path, out var actual)) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length == 0 || actual.StartsWith(@"\\", StringComparison.Ordinal) || actual.IndexOf(':', 2) >= 0 ||
            !actual.Equals(process.Path.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(actual).Equals(process.Name, StringComparison.OrdinalIgnoreCase)) return false;
        return RelativePaths.Any(relative => actual.Equals(Path.Combine(windows, relative), StringComparison.OrdinalIgnoreCase));
    }

    internal static bool SameIdentity(ProcessRecord frozen, ProcessRecord current) =>
        frozen.Id == current.Id && frozen.StartTimeUtcTicks > 0 && frozen.StartTimeUtcTicks == current.StartTimeUtcTicks &&
        frozen.SessionId == current.SessionId && !string.IsNullOrWhiteSpace(frozen.OwnerSid) &&
        string.Equals(frozen.OwnerSid, current.OwnerSid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(frozen.Name, current.Name, StringComparison.OrdinalIgnoreCase) &&
        ProtectionPolicy.TryNormalizePath(frozen.Path, out var expected) && ProtectionPolicy.TryNormalizePath(current.Path, out var actual) &&
        string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
