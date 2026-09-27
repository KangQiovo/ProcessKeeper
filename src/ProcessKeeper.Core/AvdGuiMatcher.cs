namespace ProcessKeeper.Core;

public static class AvdGuiMatcher
{
    public static bool RequiresNativeGui(ProcessRecord process, string? applicationKey = null) =>
        string.Equals(applicationKey, "known:avd", StringComparison.OrdinalIgnoreCase) ||
        process.ApplicationKey.Equals("known:avd", StringComparison.OrdinalIgnoreCase) ||
        process.Name.Equals("emulator.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Existing device-page eligibility, also allowing restore/reveal of a real Qt main window.</summary>
    public static bool IsExistingGuiWindow(WindowRecord window, bool includeHidden = false) =>
        IsQtMainWindow(window) && (window.IsVisible || includeHidden) &&
        window.Title.Contains("Android Emulator", StringComparison.OrdinalIgnoreCase);

    private static bool IsQtMainWindow(WindowRecord window) =>
        window.Handle != 0 && window.IsTopLevel && !window.IsCloaked && !window.IsToolWindow &&
        window.HasUsableBounds && (window.HasCaption || window.HasAppWindowStyle) &&
        !string.IsNullOrWhiteSpace(window.Title) &&
        window.ClassName.StartsWith("Qt", StringComparison.OrdinalIgnoreCase) &&
        window.ClassName.Contains("QWindow", StringComparison.OrdinalIgnoreCase);

    public static bool IsGuiWindow(WindowRecord window, string avdName, int port)
    {
        // Android Emulator's own desktop UI is a Qt QWindow. A console, terminal,
        // generic launcher or an unrelated titled window is never launch evidence.
        if (!IsQtMainWindow(window) || !window.IsVisible || window.IsMinimized) return false;
        return window.Title.Contains(avdName, StringComparison.OrdinalIgnoreCase) ||
            (window.Title.Contains("Android Emulator", StringComparison.OrdinalIgnoreCase) &&
             System.Text.RegularExpressions.Regex.IsMatch(window.Title, $@"(?<!\d){port}(?!\d)"));
    }

    public static bool IsLaunchMember(ProcessRecord process, AvdRestartPlan plan, IAvdStartedProcess launched)
    {
        var root = launched.Identity;
        if (process.StartTimeUtcTicks <= 0 || process.SessionId != plan.Engine.SessionId ||
            !process.OwnerSid.Equals(plan.Engine.OwnerSid, StringComparison.Ordinal) ||
            process.Name.Contains("headless", StringComparison.OrdinalIgnoreCase)) return false;
        if (process.Id == root.Id)
            return process.StartTimeUtcTicks == root.StartTimeUtcTicks && SamePath(process.Path, plan.Launcher.Path);
        if (process.ParentId != root.Id || process.StartTimeUtcTicks < root.StartTimeUtcTicks ||
            !SamePath(process.Path, plan.GuiEnginePath)) return false;
        // The root handle remains open. A later reuse of its numeric PID cannot
        // produce a child whose creation time lies within this root's lifetime.
        return !launched.HasExited || launched.ExitTimeUtcTicks is { } exited && process.StartTimeUtcTicks <= exited;
    }

    public static bool IdentifiesAvd(IReadOnlyList<string> arguments, string expectedName, int expectedPort)
    {
        string? name = null;
        int? port = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "-avd")
            {
                if (name is not null || ++index >= arguments.Count) return false;
                name = arguments[index];
            }
            else if (argument.StartsWith('@'))
            {
                if (name is not null) return false;
                name = argument[1..];
            }
            else if (argument is "-port" or "-ports")
            {
                if (port is not null || ++index >= arguments.Count ||
                    !int.TryParse(arguments[index].Split(',')[0], out var number)) return false;
                port = number;
            }
        }
        return string.Equals(name, expectedName, StringComparison.Ordinal) && port == expectedPort;
    }

    internal static bool SamePath(string left, string right) =>
        ProtectionPolicy.TryNormalizePath(left, out var normalizedLeft) && ProtectionPolicy.TryNormalizePath(right, out var normalizedRight) &&
        normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
}
