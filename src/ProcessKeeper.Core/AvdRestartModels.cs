namespace ProcessKeeper.Core;

public sealed record AvdRestartPlan(
    string AvdName,
    ProcessRecord Launcher,
    ProcessRecord Engine,
    int ConsolePort,
    IReadOnlyList<string> SourceArguments,
    IReadOnlyList<string> LaunchArguments,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Changes,
    string LauncherSha256)
{
    public string UserProfile => Environment.GetValueOrDefault("USERPROFILE") ?? "";
    public string GuiEnginePath => Engine.Path.Replace("-headless.exe", ".exe", StringComparison.OrdinalIgnoreCase);
    public int AdbPort
    {
        get
        {
            for (int index = 0; index + 1 < LaunchArguments.Count; index++)
                if (LaunchArguments[index] == "-ports" && LaunchArguments[index + 1].Split(',') is { Length: 2 } ports && int.TryParse(ports[1], out var port)) return port;
            return ConsolePort + 1;
        }
    }
}

public sealed record AvdDiscoveryResult(IReadOnlyList<AvdRestartPlan> Plans, IReadOnlyList<string> Notices);
public sealed record AvdRestartProgress(string Stage, string Message);
public sealed record AvdRestartResult(bool Success, string Message, bool ShutdownRequested, bool LaunchStarted,
    ProcessRecord? VisibleProcess = null, WindowRecord? VisibleWindow = null);

public sealed record AvdRestartTiming(TimeSpan ExitTimeout, TimeSpan WindowTimeout, TimeSpan PollInterval, int StableSamples = 3)
{
    public static AvdRestartTiming Default { get; } = new(TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(120), TimeSpan.FromMilliseconds(500));
}

/// <summary>The launched root stays held until verification ends, so a reused parent PID cannot establish ancestry.</summary>
public interface IAvdStartedProcess : IDisposable
{
    ProcessRecord Identity { get; }
    bool HasExited { get; }
    long? ExitTimeUtcTicks { get; }
}

/// <summary>OS boundary for the restart workflow; fixtures provide only owned processes and windows.</summary>
public interface IAvdRestartHost
{
    ProcessSnapshot Capture();
    IReadOnlyList<string> ReadArguments(ProcessRecord process);
    IReadOnlyDictionary<string, string> ReadEnvironment(ProcessRecord process);
    void ValidateIdentity(ProcessRecord process);
    IReadOnlyList<AvdPortListener> ReadListeners();
    Task<string> GetAvdNameAsync(ProcessRecord process, int port, string userProfile, CancellationToken cancellationToken);
    Task ShutdownAsync(ProcessRecord process, int port, string avdName, string userProfile, CancellationToken cancellationToken);
    string FileHash(string path);
    bool FileExists(string path);
    IAvdStartedProcess Launch(AvdRestartPlan plan);
}
