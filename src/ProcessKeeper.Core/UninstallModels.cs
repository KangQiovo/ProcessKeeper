namespace ProcessKeeper.Core;

public enum UninstallMode { Normal, RegisteredQuiet }
public enum UninstallOutcome { NotStarted, RegistrationRemoved, StillRegistered, MonitoringStopped, VerificationUnavailable }
public sealed record UninstallLocator(string Hive, int View, string Key);
public sealed record UninstallCommand(string Executable, string Arguments, bool IsMsi, string ProductCode = "");
public sealed record UninstallEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public string IconPath { get; init; } = "";
    public IReadOnlyList<string> ApplicationPaths { get; init; } = Array.Empty<string>();
    public UninstallApplicationIdentity? ApplicationIdentity { get; init; }
    public bool HasDisplayName { get; init; } = true;
    public bool IsHidden { get; init; }
    public bool IsSystem { get; init; }
    public bool RegistrationNoRemove { get; init; }
    public bool IsRecommended { get; init; }
    public UninstallRecommendation? Recommendation { get; init; }
    public bool CanUninstall { get; init; }
    public bool CanQuietUninstall { get; init; }
    public string ReadOnlyReason { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string ExecutableIdentity { get; init; } = "";
    public string QuietExecutableIdentity { get; init; } = "";
    public string ReviewedExecutableSha256 { get; init; } = "";
    public UninstallMode? ReviewedMode { get; init; }
    public UninstallLocator Locator { get; init; } = new("", 0, "");
    public UninstallCommand? Command { get; init; }
    public UninstallCommand? QuietCommand { get; init; }
    public string Registration => Locator.Hive + " | " + Locator.View + " | " + Locator.Key;
}
public sealed record UninstallSnapshot
{
    public IReadOnlyList<UninstallEntry> Entries { get; init; } = Array.Empty<UninstallEntry>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool Truncated { get; init; }
}
public sealed record UninstallExecution(bool Started, bool Exited, int? ExitCode, bool MonitoringStopped = false);
public sealed record UninstallResult(UninstallOutcome Outcome, string Message, bool Started = false, int? ExitCode = null, bool EmptyDirectoryRemoved = false);
public sealed record UninstallReview(UninstallEntry Entry, string SignatureInformation);

/// <summary>Execution must revalidate the registry and executable immediately before starting it. Never stops the target.</summary>
public interface IUninstallBackend
{
    UninstallSnapshot Scan(CancellationToken token);
    UninstallEntry? Read(UninstallLocator locator, CancellationToken token);
    UninstallReview Review(UninstallEntry expected, UninstallMode mode, CancellationToken token);
    Task<UninstallExecution> ExecuteAsync(UninstallEntry expected, UninstallMode mode, CancellationToken token);
}

public static class UninstallPresentation
{
    public static string EmptyDirectoryNotice => L.T("卸载完成并核实注册项已移除后，只会清理已确认归属且为空的安装目录；其他文件和上级目录会保留。");
    public static bool Matches(UninstallEntry entry, string query) =>
        ApplicationSearch.Contains(entry.Name, query) || ApplicationSearch.Contains(entry.Publisher, query) ||
        ApplicationSearch.Contains(entry.InstallLocation, query) || ApplicationSearch.Contains(entry.Registration, query) ||
        ApplicationSearch.Contains(entry.Command?.Executable, query) || ApplicationSearch.Contains(entry.Command?.Arguments, query);
    public static string Summary(UninstallEntry entry) => string.Join(" | ", new[] {
        entry.IsHidden ? L.T("隐藏注册项") : "", entry.Publisher, entry.Version, entry.Locator.Hive }.Where(s => s.Length > 0));
    public static string Confirmation(UninstallEntry entry, UninstallMode mode)
    {
        var command = mode == UninstallMode.Normal ? entry.Command : entry.QuietCommand;
        return entry.Name + "\n" + Summary(entry) + "\n\n" + command?.Executable + "\n" + command?.Arguments + "\n\n" +
            L.T(mode == UninstallMode.Normal ? "将运行此软件注册的卸载程序，请在其页面中完成操作。" :
                "将使用软件注册的静默卸载命令，可能不再显示软件自身的确认页面。") + "\n" +
            L.T("请先保存工作。卸载程序可能请求重启；取消等待不会停止已启动的卸载程序。") + "\n" +
            EmptyDirectoryNotice;
    }
}
